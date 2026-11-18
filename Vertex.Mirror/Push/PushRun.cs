// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;

namespace Vertex.Mirror.Push
{
    /// <summary>
    /// Vertex -> another version control system: replays every revision of a Vertex branch that the target doesn't
    /// have yet, oldest first, one commit or changelist each, then waits for new ones.
    /// </summary>
    /// <remarks>
    /// Any failure (network, a rejected git push, a refused Perforce submit) is logged and the target is re-synced
    /// from its own history, so the next cycle continues from the last revision that really landed. Nothing is
    /// skipped and nothing is pushed twice.
    /// </remarks>
    public sealed class PushRun
    {
        /// <summary>Revisions read per call; the target is published (git push) after each batch.</summary>
        private const int BatchSize = 100;

        private const int DownloadParallelism = 8;

        private readonly VertexConnection _vertex;
        private readonly IPushTarget _target;
        private readonly MirrorLog _log;
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        private readonly PhaseStats _listTimer = new("Read revision + its changes");
        private readonly PhaseStats _downloadTimer = new("Download changed files");
        private readonly PhaseStats _commitTimer = new("Commit / submit");
        private readonly PhaseStats _publishTimer = new("Publish (git push)");

        private long _pushed;
        private long _bytes;

        public PushRun(VertexConnection vertex, IPushTarget target, MirrorLog log)
        {
            _vertex = vertex;
            _target = target;
            _log = log;
        }

        /// <returns>The number of problems found.</returns>
        public async Task<long> RunAsync(TimeSpan interval, TimeSpan? duration, bool once, CancellationToken ct)
        {
            AnsiConsole.MarkupLine($"[grey]Opening {Markup.Escape(_target.Describe)}...[/]");
            long last = await _target.SyncAsync(ct);
            _log.Info($"Pushing {_vertex.Describe} on {_vertex.Server} to {_target.Describe}, from revision #{last + 1}" +
                      (once ? ", until it's caught up." : $", checking every {Format.Duration(interval)} {(duration is null ? "until Ctrl+C" : $"for {Format.Duration(duration.Value)}")}."));

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (duration is TimeSpan limit) stop.CancelAfter(limit);
            CancellationToken token = stop.Token;

            Stopwatch clock = Stopwatch.StartNew();
            bool caughtUp = false;
            bool resync = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // After a failure, start again from what the target really holds.
                        if (resync)
                        {
                            long synced = await _target.SyncAsync(token);
                            if (synced != last) _log.Info($"{_target.Describe} holds revisions up to #{synced}; continuing from there.");
                            last = synced;
                            resync = false;
                        }

                        last = await CatchUpAsync(last, token);
                        if (!caughtUp)
                        {
                            caughtUp = true;
                            _log.Info($"Caught up at revision #{last}.{(once ? "" : " Waiting for new revisions.")}");
                        }
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested && ex is not MirrorSetupException)
                    {
                        _log.Problem($"Push after revision #{last} failed: {VertexConnection.DescribeError(ex)}");
                        resync = true;
                    }

                    if (once) break;
                    await Task.Delay(interval, token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // --duration passed or Ctrl+C. Commits made but not yet pushed are made again by the next run.
            }

            AnsiConsole.WriteLine();
            PhaseStats.Print([_listTimer, _downloadTimer, _commitTimer, _publishTimer]);
            string summary = $"Pushed {_pushed:N0} revision(s) ({Format.Bytes(_bytes)} downloaded) to {_target.Describe} in {Format.Duration(clock.Elapsed)}; " +
                             $"last revision #{last}, {_log.Problems:N0} problem(s).";
            _log.Write(summary);
            AnsiConsole.MarkupLine(_log.Problems == 0 ? $"[green]{Markup.Escape(summary)}[/]" : $"[red]{Markup.Escape(summary)}[/] [grey]See {Markup.Escape(_log.FilePath)}.[/]");
            return _log.Problems;
        }

        /// <summary>
        /// Pushes every revision after <paramref name="last"/>, publishing after each batch.
        /// </summary>
        /// <returns>The last revision pushed.</returns>
        private async Task<long> CatchUpAsync(long last, CancellationToken ct)
        {
            while (true)
            {
                IReadOnlyList<RevisionSummary> batch = await _vertex.Revisions.GetRevisionsAsync(_vertex.ProjectId, _vertex.BranchId, last, BatchSize, ct);
                foreach (RevisionSummary revision in batch)
                {
                    await PushRevisionAsync(revision, ct);
                    last = revision.Number;
                }

                if (batch.Count > 0)
                {
                    Stopwatch publish = Stopwatch.StartNew();
                    await _target.PublishAsync(ct);
                    _publishTimer.Record(publish.Elapsed);
                }

                if (batch.Count < BatchSize) return last;
            }
        }

        private async Task PushRevisionAsync(RevisionSummary revision, CancellationToken ct)
        {
            Stopwatch phase = Stopwatch.StartNew();
            IReadOnlyList<RevisionChange> all = await _vertex.Revisions.GetRevisionChangesAsync(_vertex.ProjectId, _vertex.BranchId, revision.Number, ct);
            _listTimer.Record(phase.Elapsed);

            List<RevisionChange> changes = new(all.Count);
            foreach (RevisionChange change in all)
            {
                string reason = "it isn't a valid path";
                if (RepositoryPath.IsValid(change.Path) && _target.Accepts(change.Path, out reason)) changes.Add(change);
                else if (_reported.Add(change.Path)) _log.Info($"Skipped '{change.Path}' in #{revision.Number}: {reason}.");
            }

            await _target.BeforeWriteAsync(changes, ct);

            phase.Restart();
            long bytes = 0;
            await Parallel.ForEachAsync(changes.Where(c => c.FileId is not null), new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = ct },
                async (change, token) =>
                {
                    string target = LocalPath(change.Path);
                    if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                    DownloadResult result = await _vertex.Files.DownloadAsync(change.FileId!, target, token);
                    Interlocked.Add(ref bytes, result.CompressedBytesDownloaded);
                });
            _downloadTimer.Record(phase.Elapsed);

            foreach (RevisionChange change in changes.Where(c => c.FileId is null))
            {
                string target = LocalPath(change.Path);
                if (!File.Exists(target)) continue;
                File.SetAttributes(target, FileAttributes.Normal);
                File.Delete(target);
            }

            phase.Restart();
            string? id = await _target.CommitAsync(revision, changes, ct);
            _commitTimer.Record(phase.Elapsed);
            _pushed++;
            _bytes += bytes;

            string result = id is null ? "[grey]nothing to submit[/]" : $"-> {Markup.Escape(id)}";
            AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss}[/] #{revision.Number,-6} {result} ({changes.Count:N0} file(s), {Format.Bytes(bytes)}) " +
                                   $"[grey]{Markup.Escape(revision.Username)}:[/] {Markup.Escape(Format.FirstLine(revision.Message))}");
        }

        private string LocalPath(string path)
        {
            return Path.Combine(_target.Root, path.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
