// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using System.Diagnostics;
using Vertex.Core.Net.Interfaces;
using Vertex.Server;
using Vertex.Tests.Metrics;
using Vertex.Tests.Scenarios.Workers;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// How fast a change reaches everyone watching: many subscribers on the event stream while one workspace locks
    /// and releases a file ten times a second. Each delivery is timed from just before the lock call to arrival, so it
    /// includes the write, the commit, the notification fan-out and the proxy. Every subscriber must see every event.
    /// </summary>
    public sealed class EventFanoutScenario : IScenario
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

        public string Name => "events";

        public string Description => "Event stream fan-out: delivery latency from a lock to every subscriber, and no missed events.";

        public async Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct)
        {
            int count = Math.Max(1, context.Profile.Subscribers);
            using LockInterface locks = context.Locks();
            Guid workspace = Guid.NewGuid();
            string folder = $"{context.PathPrefix}/events";
            string warmup = $"l:{folder}/Warmup.uasset";

            // Starting every subscriber from the same seq means they all get the same events, however long connecting takes.
            long start = (await locks.GetLockEventsAsync(context.ProjectId, context.BranchId, 0, ct)).LatestSeq;
            ConcurrentDictionary<string, long> sent = new(StringComparer.Ordinal);
            LatencyRecorder delivery = result.Timer($"Event delivery, lock call to subscriber ({count} subscribers)");
            LatencyRecorder streamErrors = result.Timer("Event stream (errors only)");
            long connectStart = Stopwatch.GetTimestamp();
            LatencyRecorder connect = result.Timer("Subscribe until first event arrives");

            List<EventSubscriber> subscribers = Enumerable.Range(0, count).Select(_ => new EventSubscriber(context, start, streamErrors, (subscriber, message, received) =>
            {
                if (message.Lock is null) return;
                foreach (string path in message.Lock.Paths)
                {
                    string key = (message.Lock.Released ? "r:" : "l:") + path;
                    if (!subscriber.Seen.Add(key)) continue;
                    if (key == warmup) connect.Record(Stopwatch.GetElapsedTime(connectStart, received));
                    else if (sent.TryGetValue(key, out long sentAt)) delivery.Record(Stopwatch.GetElapsedTime(sentAt, received));
                }
            })).ToList();

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task[] listening = subscribers.Select(s => Task.Run(() => s.RunAsync(stop.Token), CancellationToken.None)).ToArray();
            try
            {
                await locks.LockAsync(context.ProjectId, context.BranchId, workspace, "bench-events", [warmup[2..]], ct);
                bool ready = await WaitAsync(() => connect.Count >= count, TimeSpan.FromSeconds(60), ct);
                result.Check(ready, $"Only {connect.Count} of {count} subscribers received the first event within 60 s.");

                LatencyRecorder writer = result.Timer("Writer lock/unlock call");
                Stopwatch clock = Stopwatch.StartNew();
                using PeriodicTimer ticker = new(Interval);
                int n = 0;
                while (clock.Elapsed < context.Profile.Duration && await ticker.WaitForNextTickAsync(ct))
                {
                    string path = $"{folder}/File{n++:D6}.uasset";
                    sent["l:" + path] = Stopwatch.GetTimestamp();
                    await writer.TryTimeAsync(() => locks.LockAsync(context.ProjectId, context.BranchId, workspace, "bench-events", [path], ct), ct);
                    sent["r:" + path] = Stopwatch.GetTimestamp();
                    await writer.TryTimeAsync(() => locks.UnlockAsync(context.ProjectId, context.BranchId, workspace, [path], ct: ct), ct);
                }

                TimeSpan elapsed = clock.Elapsed;
                int expected = sent.Count + 1;
                bool drained = await WaitAsync(() => subscribers.All(s => s.Seen.Count >= expected), TimeSpan.FromSeconds(15), ct);
                long missing = subscribers.Sum(s => Math.Max(0, expected - s.Seen.Count));

                result.Rate("Events written", sent.Count, elapsed, "events");
                result.Rate("Deliveries", delivery.Count, elapsed, "deliveries");
                result.Figure("Reconnects", subscribers.Sum(s => s.Reconnects).ToString("N0"));
                result.Check(drained && missing == 0, $"{missing:N0} event deliveries never arrived ({Format.Percent(missing, (double)expected * count)}).");
                result.Check(writer.ErrorCount == 0, $"{writer.ErrorCount} writer calls failed.");
            }
            finally
            {
                await stop.CancelAsync();
                await Task.WhenAll(listening);
                foreach (EventSubscriber subscriber in subscribers) subscriber.Dispose();
                await locks.UnlockAsync(context.ProjectId, context.BranchId, workspace, [warmup[2..]], ct: CancellationToken.None);
            }
        }

        private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed > timeout) return false;
                await Task.Delay(50, ct);
            }

            return true;
        }
    }
}
