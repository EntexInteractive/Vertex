// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net;
using Vertex.Core.Net.Interfaces;
using Vertex.Server;
using Vertex.Tests.Metrics;
using Vertex.Tests.Scenarios.Workers;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// Lock and release traffic from many workspaces: half on their own files, half fighting over a small shared set.
    /// Then one workspace locks the largest batch a request allows and reads the branch's full lock list.
    /// </summary>
    public sealed class LockScenario : IScenario
    {
        public string Name => "locks";

        public string Description => "Lock/unlock churn from many workspaces (with contention on shared files), then a maximum-size batch.";

        public async Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct)
        {
            int clients = context.Profile.Clients;
            LatencyRecorder lockTimer = result.Timer($"Lock ({clients} workspaces)");
            LatencyRecorder unlockTimer = result.Timer($"Unlock ({clients} workspaces)");
            List<LockWorker> workers = Enumerable.Range(0, clients).Select(i => new LockWorker(context, i, i % 2 == 1, lockTimer, unlockTimer)).ToList();
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                TimeSpan duration = context.Profile.Duration;
                await Task.WhenAll(workers.Select(w => w.RunAsync(() => clock.Elapsed >= duration, ct)));
                TimeSpan elapsed = clock.Elapsed;
                ct.ThrowIfCancellationRequested();

                long granted = workers.Sum(w => w.Granted);
                long held = workers.Sum(w => w.Held);
                result.Rate("Files locked", granted, elapsed, "locks");
                result.Rate("Lock calls", lockTimer.Count, elapsed, "calls");
                result.Figure("Contended", $"{held:N0} requests found the file held by another workspace ({Format.Percent(held, held + granted)})", held);
                result.Check(granted > 0, "No lock was granted.");
                result.Check(lockTimer.ErrorCount == 0 && unlockTimer.ErrorCount == 0, "Lock calls failed.");
            }
            finally
            {
                foreach (LockWorker worker in workers) worker.Dispose();
            }

            // The biggest batch one request carries, like locking a whole folder of saved assets.
            int batch = FileTransferLimits.MaximumPathsPerLockRequest;
            string[] paths = Enumerable.Range(0, batch).Select(i => $"{context.PathPrefix}/locks/batch/File{i:D5}.uasset").ToArray();
            Guid workspace = Guid.NewGuid();
            using LockInterface locks = context.Locks();
            List<LockResult> results = await result.Timer($"Lock {batch:N0} files in one call")
                .TimeAsync(() => locks.LockAsync(context.ProjectId, context.BranchId, workspace, "bench-batch", paths, ct));
            result.Check(results.Count(r => r.Status == LockStatus.Granted) == batch, $"Only {results.Count(r => r.Status == LockStatus.Granted):N0} of {batch:N0} batch locks were granted.");

            GetLockEventsResponse list = await result.Timer("Full lock list (GetLockEvents after 0)")
                .TimeAsync(() => locks.GetLockEventsAsync(context.ProjectId, context.BranchId, 0, ct));
            result.Check(list.Locks.Count >= batch, $"The lock list has {list.Locks.Count:N0} locks with {batch:N0} held.");

            List<string> released = await result.Timer($"Unlock {batch:N0} files in one call")
                .TimeAsync(() => locks.UnlockAsync(context.ProjectId, context.BranchId, workspace, paths, ct: ct));
            result.Check(released.Count == batch, $"Released {released.Count:N0} of {batch:N0} batch locks.");
        }
    }
}
