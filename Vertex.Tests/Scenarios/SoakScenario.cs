// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Server;
using Vertex.Tests.Metrics;
using Vertex.Tests.Scenarios.Workers;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// A studio's steady state, for watching dashboards: submitters, lockers, event subscribers, workspaces syncing
    /// and pulling an asset, all at once until --duration passes or Ctrl+C. Prints a line every 10 seconds.
    /// </summary>
    public sealed class SoakScenario : IScenario
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(10);

        public string Name => "soak";

        public string Description => "Mixed steady load (submits, locks, event streams, syncs, downloads) until --duration or Ctrl+C.";

        public async Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct)
        {
            int quarter = Math.Max(1, context.Profile.Clients / 4);
            ChunkedFile asset = await context.GetSoakAssetAsync(ct);

            LatencyRecorder upload = result.Timer("Upload 1-20 small files");
            LatencyRecorder submit = result.Timer("Submit revision");
            LatencyRecorder lockTimer = result.Timer("Lock");
            LatencyRecorder unlockTimer = result.Timer("Unlock");
            LatencyRecorder sync = result.Timer("Incremental sync listing");
            LatencyRecorder poll = result.Timer("Lock poll (older clients)");
            LatencyRecorder download = result.Timer($"Download {Format.Bytes(asset.Size)} asset");
            LatencyRecorder streamErrors = result.Timer("Event stream (errors only)");

            List<SubmitWorker> submitters = Enumerable.Range(0, quarter).Select(i => new SubmitWorker(context, 500 + i, upload, submit)).ToList();
            List<LockWorker> lockers = Enumerable.Range(0, quarter).Select(i => new LockWorker(context, 500 + i, i % 2 == 1, lockTimer, unlockTimer)).ToList();
            using LockInterface seqSource = context.Locks();
            long startSeq = (await seqSource.GetLockEventsAsync(context.ProjectId, context.BranchId, 0, ct)).LatestSeq;
            List<EventSubscriber> subscribers = Enumerable.Range(0, context.Profile.Subscribers).Select(_ => new EventSubscriber(context, startSeq, streamErrors, null)).ToList();

            // Without --duration a soak runs until Ctrl+C, and that is a normal end, not a cancelled run.
            TimeSpan? limit = context.SoakDuration;
            Stopwatch clock = Stopwatch.StartNew();
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (limit is TimeSpan l) stop.CancelAfter(l);
            CancellationToken token = stop.Token;
            AnsiConsole.MarkupLine($"[grey]{quarter} submitters (think {context.Profile.SubmitThink}), {quarter} lockers (think {context.Profile.LockThink}), " +
                                   $"{subscribers.Count} subscribers, {quarter} syncing workspaces, 1 downloader. " +
                                   $"{(limit is null ? "Ctrl+C to stop." : $"Running for {Format.Duration(limit.Value)}.")}[/]");

            List<Task> tasks = new();
            tasks.AddRange(submitters.Select(w => Swallow(w.RunAsync(() => false, token), token)));
            tasks.AddRange(lockers.Select(w => Swallow(w.RunAsync(() => false, token), token)));
            tasks.AddRange(subscribers.Select(s => s.RunAsync(token)));
            tasks.AddRange(Enumerable.Range(0, quarter).Select(_ => Swallow(SyncLoopAsync(context, sync, poll, token), token)));
            tasks.Add(Swallow(DownloadLoopAsync(context, asset, download, token), token));
            tasks.Add(Swallow(ReportLoopAsync(clock, submit, lockTimer, sync, download, result, subscribers, token), token));

            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                foreach (SubmitWorker w in submitters) w.Dispose();
                foreach (LockWorker w in lockers) w.Dispose();
                foreach (EventSubscriber s in subscribers) s.Dispose();
            }

            TimeSpan elapsed = clock.Elapsed;
            result.Rate("Revisions", submitters.Sum(w => w.RevisionsSubmitted), elapsed, "revisions");
            result.Rate("Files locked", lockers.Sum(w => w.Granted), elapsed, "locks");
            result.Rate("Events delivered", subscribers.Sum(s => s.Received), elapsed, "events");
            result.Figure("Stream reconnects", subscribers.Sum(s => s.Reconnects).ToString("N0"));
            result.Figure("Think time", $"{context.Profile.SubmitThink} between submits, {context.Profile.LockThink} between locks");
            result.Figure("Duration", Format.Duration(elapsed), elapsed.TotalSeconds, "s");
            result.Check(result.TotalErrors == 0, $"{result.TotalErrors:N0} calls failed during the soak.");
        }

        /// <summary>
        /// A workspace that keeps up with the branch: incremental listings, plus the lock poll older clients make.
        /// </summary>
        private static async Task SyncLoopAsync(BenchContext context, LatencyRecorder sync, LatencyRecorder poll, CancellationToken ct)
        {
            using RevisionInterface revisions = context.Revisions();
            using LockInterface locks = context.Locks();
            long revision = 0;
            try
            {
                // Asking for changes after a far-future revision returns just the head number.
                revision = (await revisions.GetHeadChangesAsync(context.ProjectId, context.BranchId, long.MaxValue / 2, ct)).HeadRevision;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                sync.Error(ex);
            }

            long seq = 0;
            while (!ct.IsCancellationRequested)
            {
                (bool ok, HeadChanges? changes) = await sync.TryTimeAsync(() => revisions.GetHeadChangesAsync(context.ProjectId, context.BranchId, revision, ct), ct);
                if (ok) revision = changes!.HeadRevision;
                (bool polled, GetLockEventsResponse? events) = await poll.TryTimeAsync(() => locks.GetLockEventsAsync(context.ProjectId, context.BranchId, seq, ct), ct);
                if (polled) seq = events!.LatestSeq;
                await Task.Delay(2000, ct);
            }
        }

        private static async Task DownloadLoopAsync(BenchContext context, ChunkedFile asset, LatencyRecorder download, CancellationToken ct)
        {
            using FilesInterface files = context.Files(4);
            string folder = context.NewDirectory("soak-download");
            while (!ct.IsCancellationRequested)
            {
                string target = Path.Combine(folder, "Soak.uasset");
                await download.TryTimeAsync(() => files.DownloadAsync(asset.FileId, target, ct), ct);
                File.Delete(target);
                await Task.Delay(15_000, ct);
            }
        }

        private static async Task ReportLoopAsync(Stopwatch clock, LatencyRecorder submit, LatencyRecorder locks, LatencyRecorder sync, LatencyRecorder download,
            ScenarioResult result, List<EventSubscriber> subscribers, CancellationToken ct)
        {
            int submitCursor = 0, lockCursor = 0, syncCursor = 0, downloadCursor = 0;
            long lastEvents = 0;
            using PeriodicTimer ticker = new(ReportInterval);
            while (await ticker.WaitForNextTickAsync(ct))
            {
                LatencySummary s = submit.SummarizeSince(ref submitCursor);
                LatencySummary l = locks.SummarizeSince(ref lockCursor);
                LatencySummary y = sync.SummarizeSince(ref syncCursor);
                LatencySummary d = download.SummarizeSince(ref downloadCursor);
                long events = subscribers.Sum(x => x.Received);
                double seconds = ReportInterval.TotalSeconds;
                string errors = result.TotalErrors == 0 ? "0" : $"[red]{result.TotalErrors:N0}[/]";
                AnsiConsole.MarkupLine($"[grey]{DateTime.UtcNow:HH:mm:ss}[/] {Format.Duration(clock.Elapsed),8} | " +
                                       $"submit {s.Count / seconds,6:N1}/s p95 {Format.Milliseconds(s.P95),6} ms | " +
                                       $"lock {l.Count / seconds,6:N1}/s p95 {Format.Milliseconds(l.P95),6} ms | " +
                                       $"sync p95 {Format.Milliseconds(y.P95),6} ms | " +
                                       $"download {(d.Count == 0 ? "-" : Format.Milliseconds(d.Mean) + " ms"),9} | " +
                                       $"events {(events - lastEvents) / seconds,7:N1}/s | errors {errors}");
                lastEvents = events;
            }
        }

        private static async Task Swallow(Task task, CancellationToken ct)
        {
            try
            {
                await task;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Stopped.
            }
        }
    }
}
