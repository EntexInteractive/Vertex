// Copyright 2026 Entex Interactive

namespace Vertex.Tests.Metrics
{
    /// <summary>
    /// A headline number: throughput, rate or count. <see cref="Number"/> is the value in <see cref="Unit"/> for
    /// machine-readable reports.
    /// </summary>
    public sealed record Figure(string Name, string Value, double? Number = null, string? Unit = null);

    /// <summary>
    /// Everything one scenario measured, and when, so it can be lined up with the server's own metrics.
    /// </summary>
    public sealed class ScenarioResult
    {
        private readonly Lock _gate = new();
        private readonly List<LatencyRecorder> _timings = new();
        private readonly List<Figure> _figures = new();
        private readonly List<string> _failures = new();
        private readonly List<string> _notes = new();

        public ScenarioResult(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }

        public string Description { get; }

        public DateTimeOffset Started { get; set; }

        public DateTimeOffset Ended { get; set; }

        public bool Cancelled { get; set; }

        public bool Passed => _failures.Count == 0 && !Cancelled;

        public IReadOnlyList<LatencyRecorder> Timings
        {
            get
            {
                lock (_gate) return _timings.ToArray();
            }
        }

        public IReadOnlyList<Figure> Figures
        {
            get
            {
                lock (_gate) return _figures.ToArray();
            }
        }

        public IReadOnlyList<string> Failures
        {
            get
            {
                lock (_gate) return _failures.ToArray();
            }
        }

        public IReadOnlyList<string> Notes
        {
            get
            {
                lock (_gate) return _notes.ToArray();
            }
        }

        public long TotalErrors => Timings.Sum(t => t.ErrorCount);

        /// <summary>
        /// The recorder for a named call, created on first use.
        /// </summary>
        public LatencyRecorder Timer(string name)
        {
            lock (_gate)
            {
                LatencyRecorder? existing = _timings.FirstOrDefault(t => t.Name == name);
                if (existing is not null) return existing;
                LatencyRecorder created = new(name);
                _timings.Add(created);
                return created;
            }
        }

        public void Figure(string name, string value, double? number = null, string? unit = null)
        {
            lock (_gate) _figures.Add(new Figure(name, value, number, unit));
        }

        /// <summary>
        /// Records a transfer rate, e.g. "312.4 MB/s (2.0 GB in 6.55 s)".
        /// </summary>
        public void Throughput(string name, long bytes, TimeSpan elapsed, string? extra = null)
        {
            double rate = elapsed > TimeSpan.Zero ? bytes / (1024.0 * 1024) / elapsed.TotalSeconds : 0;
            string value = $"{Format.Rate(bytes, elapsed)} ({Format.Bytes(bytes)} in {Format.Duration(elapsed)}{(extra is null ? "" : ", " + extra)})";
            Figure(name, value, rate, "MB/s");
        }

        /// <summary>
        /// Records an operation rate, e.g. "42.0 revisions/s (2,520 in 60.00 s)".
        /// </summary>
        public void Rate(string name, long count, TimeSpan elapsed, string unit)
        {
            double rate = elapsed > TimeSpan.Zero ? count / elapsed.TotalSeconds : 0;
            Figure(name, $"{Format.PerSecond(count, elapsed, unit)} ({count:N0} in {Format.Duration(elapsed)})", rate, unit + "/s");
        }

        public void Fail(string message)
        {
            lock (_gate) _failures.Add(message);
        }

        public void Check(bool condition, string failure)
        {
            if (!condition) Fail(failure);
        }

        public void Note(string message)
        {
            lock (_gate) _notes.Add(message);
        }
    }
}
