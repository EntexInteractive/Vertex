// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net.Interfaces;
using Vertex.Server;
using Vertex.Tests.Metrics;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// The floor every other number sits on: round trips for a cheap REST call and a cheap gRPC-Web call, the cost
    /// of a fresh connection (TLS handshake, as each vx command pays), and how many cheap calls per second the server
    /// answers with many clients.
    /// </summary>
    public sealed class LatencyScenario : IScenario
    {
        private const int Samples = 50;

        public string Name => "latency";

        public string Description => "Round-trip baseline: REST and gRPC calls, new connections, and cheap-call throughput under concurrency.";

        public async Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct)
        {
            using ServerInterface server = new(context.Server, context.Token);
            using LockInterface locks = context.Locks();

            // Warm the connections so the first handshake doesn't skew the warm numbers.
            BenchContext.Unwrap(await server.GetInfoAsync(ct));
            long seq = (await locks.GetLockEventsAsync(context.ProjectId, context.BranchId, 0, ct)).LatestSeq;

            LatencyRecorder rest = result.Timer("REST GET server/info (warm)");
            for (int i = 0; i < Samples; i++)
                await rest.TryTimeAsync(async () => BenchContext.Unwrap(await server.GetInfoAsync(ct)), ct);

            LatencyRecorder grpc = result.Timer("gRPC GetLockEvents, up to date (warm)");
            for (int i = 0; i < Samples; i++)
                await grpc.TryTimeAsync(() => locks.GetLockEventsAsync(context.ProjectId, context.BranchId, seq, ct), ct);

            LatencyRecorder cold = result.Timer("New connection + REST call");
            for (int i = 0; i < 10; i++)
            {
                await cold.TryTimeAsync(async () =>
                {
                    using ServerInterface fresh = new(context.Server, context.Token);
                    return BenchContext.Unwrap(await fresh.GetInfoAsync(ct));
                }, ct);
            }

            // Many clients, each on its own connection, polling as fast as the server answers.
            int clients = context.Profile.Clients;
            TimeSpan duration = TimeSpan.FromSeconds(Math.Min(30, context.Profile.Duration.TotalSeconds));
            LatencyRecorder loaded = result.Timer($"gRPC GetLockEvents ({clients} clients)");
            Stopwatch timer = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, clients).Select(async _ =>
            {
                using LockInterface client = context.Locks();
                while (timer.Elapsed < duration && !ct.IsCancellationRequested)
                {
                    (bool ok, GetLockEventsResponse? _) = await loaded.TryTimeAsync(() => client.GetLockEventsAsync(context.ProjectId, context.BranchId, seq, ct), ct);
                    if (!ok) await Task.Delay(100, ct);
                }
            }));

            ct.ThrowIfCancellationRequested();
            result.Rate($"Cheap gRPC calls, {clients} clients", loaded.Count, timer.Elapsed, "calls");
            LatencySummary warm = grpc.Summarize();
            result.Figure("Round trip (gRPC p50)", $"{Format.Milliseconds(warm.P50)} ms", warm.P50, "ms");
            result.Check(rest.ErrorCount == 0 && grpc.ErrorCount == 0, "Cheap calls failed on an idle server.");
        }
    }
}
