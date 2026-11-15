// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net.Interfaces;
using Vertex.Server;
using Vertex.Tests.Metrics;

namespace Vertex.Tests.Scenarios.Workers
{
    /// <summary>
    /// One client on the project event stream, on its own connection, resubscribing from its last seq whenever the
    /// stream ends or drops, exactly as the workspace monitor does.
    /// </summary>
    public sealed class EventSubscriber : IDisposable
    {
        private readonly BenchContext _context;
        private readonly EventsInterface _events;
        private readonly LatencyRecorder _errors;
        private readonly Action<EventSubscriber, EventMessage, long>? _onEvent;
        private long _lastSeq;
        private long _received;
        private long _reconnects;

        /// <param name="afterSeq">Where to start; events after it are replayed, so nothing is missed while connecting.</param>
        /// <param name="errors">Counts stream failures.</param>
        /// <param name="onEvent">Called on the subscriber's own loop with each non-heartbeat event and its
        /// <see cref="Stopwatch.GetTimestamp"/> receive time.</param>
        public EventSubscriber(BenchContext context, long afterSeq, LatencyRecorder errors, Action<EventSubscriber, EventMessage, long>? onEvent)
        {
            _context = context;
            _lastSeq = afterSeq;
            _errors = errors;
            _onEvent = onEvent;
            _events = context.Events();
        }

        public long Received => Interlocked.Read(ref _received);

        public long Reconnects => Interlocked.Read(ref _reconnects);

        /// <summary>
        /// Per-subscriber state for the caller's callback; only touched on this subscriber's loop.
        /// </summary>
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Listens until <paramref name="ct"/> is cancelled; returns normally then.
        /// </summary>
        public async Task RunAsync(CancellationToken ct)
        {
            bool first = true;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!first) Interlocked.Increment(ref _reconnects);
                    first = false;
                    try
                    {
                        await foreach (EventMessage message in _events.SubscribeAsync(_context.ProjectId, _context.BranchId, _lastSeq, ct))
                        {
                            long received = Stopwatch.GetTimestamp();
                            _lastSeq = Math.Max(_lastSeq, message.Seq);
                            if (message.BodyCase is EventMessage.BodyOneofCase.Heartbeat) continue;
                            Interlocked.Increment(ref _received);
                            _onEvent?.Invoke(this, message, received);
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        _errors.Error(ex);
                        await Task.Delay(1000, ct);
                    }
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Stopped.
            }
        }

        public void Dispose()
        {
            _events.Dispose();
        }
    }
}
