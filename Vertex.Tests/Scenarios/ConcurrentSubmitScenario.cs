// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Tests.Metrics;
using Vertex.Tests.Scenarios.Workers;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// Many developers submitting small revisions to one branch at once. Submits serialize on the project's write
    /// lock, so this is where vertex.project_lock.wait shows up. Ends with one deliberately stale submit per worker,
    /// which the server must refuse.
    /// </summary>
    public sealed class ConcurrentSubmitScenario : IScenario
    {
        public string Name => "submitters";

        public string Description => "Many workspaces submitting small revisions to one branch at once, plus out-of-date submits that must be refused.";

        public async Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct)
        {
            int clients = context.Profile.Clients;
            LatencyRecorder upload = result.Timer("Upload 1-20 small files");
            LatencyRecorder submit = result.Timer($"Submit revision ({clients} concurrent workspaces)");
            List<SubmitWorker> workers = Enumerable.Range(0, clients).Select(i => new SubmitWorker(context, i, upload, submit)).ToList();
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                TimeSpan duration = context.Profile.Duration;
                await Task.WhenAll(workers.Select(w => w.RunAsync(() => clock.Elapsed >= duration, ct)));
                TimeSpan elapsed = clock.Elapsed;
                ct.ThrowIfCancellationRequested();

                long revisions = workers.Sum(w => w.RevisionsSubmitted);
                result.Rate("Revisions", revisions, elapsed, "revisions");
                result.Rate("Files submitted", workers.Sum(w => w.FilesSubmitted), elapsed, "files");
                result.Check(revisions > 0, "No revision was submitted.");
                result.Check(submit.ErrorCount == 0, $"{submit.ErrorCount} submits on non-overlapping paths failed.");

                LatencyRecorder stale = result.Timer("Out-of-date submit (must be refused)");
                bool[] refused = await Task.WhenAll(workers.Select(w => stale.TimeAsync(() => w.SubmitStaleAsync(ct))));
                result.Check(refused.All(r => r), $"{refused.Count(r => !r)} out-of-date submit(s) were accepted.");
            }
            finally
            {
                foreach (SubmitWorker worker in workers) worker.Dispose();
            }
        }
    }
}
