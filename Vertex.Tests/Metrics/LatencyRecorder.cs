// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Grpc.Core;

namespace Vertex.Tests.Metrics
{
    /// <summary>
    /// Latency percentiles of one kind of call, in milliseconds.
    /// </summary>
    public sealed record LatencySummary(string Name, int Count, long Errors, double Mean, double P50, double P95, double P99, double Max, IReadOnlyDictionary<string, long> ErrorsByKind);

    /// <summary>
    /// Collects call durations and failures from any number of threads.
    /// </summary>
    public sealed partial class LatencyRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<double> _samples = new();
        private readonly ConcurrentDictionary<string, long> _errors = new();
        private long _errorCount;

        public LatencyRecorder(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public int Count
        {
            get
            {
                lock (_gate) return _samples.Count;
            }
        }

        public long ErrorCount => Interlocked.Read(ref _errorCount);

        public void Record(TimeSpan elapsed)
        {
            RecordMilliseconds(elapsed.TotalMilliseconds);
        }

        public void RecordMilliseconds(double milliseconds)
        {
            lock (_gate) _samples.Add(milliseconds);
        }

        public void Error(Exception exception)
        {
            Error(Classify(exception));
        }

        public void Error(string kind)
        {
            _errors.AddOrUpdate(kind, 1, (_, count) => count + 1);
            Interlocked.Increment(ref _errorCount);
        }

        /// <summary>
        /// Times a call. Failures are counted and rethrown.
        /// </summary>
        public async Task<T> TimeAsync<T>(Func<Task<T>> call)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                T value = await call();
                Record(Stopwatch.GetElapsedTime(start));
                return value;
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                Error(ex);
                throw;
            }
        }

        /// <summary>
        /// Times a call. Failures are counted and swallowed (returning default) unless <paramref name="ct"/> was
        /// cancelled, so load loops keep going through errors.
        /// </summary>
        public async Task<(bool Ok, T? Value)> TryTimeAsync<T>(Func<Task<T>> call, CancellationToken ct)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                T value = await call();
                Record(Stopwatch.GetElapsedTime(start));
                return (true, value);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Error(ex);
                return (false, default);
            }
        }

        public LatencySummary Summarize()
        {
            int cursor = 0;
            return SummarizeSince(ref cursor, true);
        }

        /// <summary>
        /// Summarizes the samples recorded after <paramref name="cursor"/> and moves it to the end, for live windows.
        /// </summary>
        public LatencySummary SummarizeSince(ref int cursor, bool includeAllErrors = false)
        {
            double[] window;
            lock (_gate)
            {
                window = _samples.Skip(cursor).ToArray();
                cursor = _samples.Count;
            }

            Array.Sort(window);
            Dictionary<string, long> errors = includeAllErrors ? new Dictionary<string, long>(_errors) : new Dictionary<string, long>();
            if (window.Length == 0) return new LatencySummary(Name, 0, ErrorCount, 0, 0, 0, 0, 0, errors);

            return new LatencySummary(Name, window.Length, ErrorCount, window.Average(), Percentile(window, 0.50), Percentile(window, 0.95),
                Percentile(window, 0.99), window[^1], errors);
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            int rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }

        private static bool IsCancellation(Exception ex)
        {
            return ex is OperationCanceledException || ex is RpcException { StatusCode: StatusCode.Cancelled };
        }

        /// <summary>
        /// A short label for a failure, separating proxy/HTTP errors (429, 502...) from gRPC statuses the server sent.
        /// </summary>
        public static string Classify(Exception exception)
        {
            switch (exception)
            {
                case RpcException rpc:
                    Match match = HttpStatusPattern().Match(rpc.Status.Detail ?? string.Empty);
                    return match.Success ? $"http {match.Groups[1].Value}" : $"grpc {rpc.StatusCode}";
                case HttpRequestException { StatusCode: HttpStatusCode code }:
                    return $"http {(int)code}";
                case HttpRequestException:
                    return "network";
                case TimeoutException or TaskCanceledException:
                    return "timeout";
                case AggregateException { InnerExceptions.Count: > 0 } aggregate:
                    return Classify(aggregate.InnerExceptions[0]);
                default:
                    return exception.GetType().Name;
            }
        }

        [GeneratedRegex(@"HTTP status code: (\d{3})")]
        private static partial Regex HttpStatusPattern();
    }
}
