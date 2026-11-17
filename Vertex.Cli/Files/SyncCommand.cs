// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using System.Diagnostics;
using Vertex.Core.Client.IO;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Brings the workspace up to the latest revision of its branch: downloads files that changed on the server since
    /// the last sync and removes files that were deleted. Files changed locally are never overwritten; they are
    /// reported as conflicts with the command that resolves them.
    /// </summary>
    public sealed class SyncCommand : AsyncCommand<WorkspaceSettings>
    {
        /// <summary>Conflicts listed before "and N more".</summary>
        private const int ListedConflicts = 10;

        public override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
        {
            Stopwatch timer = Stopwatch.StartNew();
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            VWorkspace workspace = linked.Workspace;
            int parallel = Math.Max(1, settings.MaxConcurrency);
            using WorkspaceIndex index = workspace.OpenIndex();
            long.TryParse(index.GetMeta(WorkspaceIndex.SyncedRevisionKey), out long synced);

            // Scan first so local edits are known and never overwritten.
            WorkspaceStatus status = null!;
            HeadChanges head = null!;
            using RevisionInterface revisions = new(settings.ServerAddress, settings.Token);
            await AnsiConsole.Status().StartAsync("Checking for changes...", async _ =>
            {
                status = WorkspaceScanner.Scan(workspace, index, new ScanOptions { MaxConcurrency = parallel }, cancellationToken);
                head = await revisions.GetHeadChangesAsync(linked.ProjectId, linked.BranchId, synced, cancellationToken);
            });

            HashSet<string> changedLocally = new(WorkspaceScanner.PathComparer);
            foreach (WorkspaceChange change in status.Changes)
            {
                changedLocally.Add(change.Path);
                if (change.PreviousPath is not null) changedLocally.Add(change.PreviousPath);
            }

            List<HeadChange> downloads = new();
            List<string> conflicts = new();
            List<IndexEntry> upserts = new();
            List<string> deletes = new();
            foreach (HeadChange file in head.Files)
            {
                status.Entries.TryGetValue(file.Path, out IndexEntry? entry);

                // Already matches the server, e.g. the same edit made here: only the base moves.
                if (file.FileId is not null && entry?.FileId == file.FileId)
                {
                    if (entry.BaseId != file.FileId) upserts.Add(entry with { BaseId = file.FileId });
                    continue;
                }

                if (changedLocally.Contains(file.Path))
                {
                    conflicts.Add(file.Path);
                }
                else if (file.FileId is null)
                {
                    if (entry is not null)
                    {
                        string fullPath = workspace.ToFullPath(file.Path);
                        FileSystemUtility.MakeWritable(fullPath);
                        File.Delete(fullPath);
                        deletes.Add(entry.Path);
                    }
                }
                else
                {
                    downloads.Add(file);
                }
            }

            ConcurrentBag<IndexEntry> downloaded = new();
            long bytes = 0;
            try
            {
                using FilesInterface files = new(settings.ServerAddress, settings.Token) { MaxConcurrency = 2 };
                if (downloads.Count > 0) await AnsiConsole.Progress().StartAsync(async progress =>
                {
                    ProgressTask task = progress.AddTask("Downloading", maxValue: Math.Max(1, downloads.Count));
                    await Parallel.ForEachAsync(downloads, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (file, ct) =>
                    {
                        string fullPath = workspace.ToFullPath(file.Path);
                        DownloadResult result = await files.DownloadAsync(file.FileId!, fullPath, ct);
                        Interlocked.Add(ref bytes, result.CompressedBytesDownloaded);

                        FileInfo info = new(fullPath);
                        downloaded.Add(new IndexEntry(status.Entries.TryGetValue(file.Path, out IndexEntry? entry) ? entry.Path : file.Path,
                            info.Length, info.LastWriteTimeUtc.Ticks, file.FileId!, file.FileId, DateTime.UtcNow.Ticks));
                        task.Increment(1);
                    });
                });
            }
            finally
            {
                // Record whatever arrived, even when a download failed part way.
                index.Apply(upserts.Concat(downloaded), deletes);
            }

            // Skipped conflicts must come back on the next sync, so only a clean sync moves the synced revision.
            if (conflicts.Count == 0) index.SetMeta(WorkspaceIndex.SyncedRevisionKey, head.HeadRevision.ToString());

            // Replacing a file drops its read-only flag, so check the files just written against everyone's locks.
            using LockInterface lockClient = new(settings.ServerAddress, settings.Token);
            LockReport locks = await LockOutput.Open(linked, index, lockClient, settings)
                .ReconcileAsync(status.Changes, downloaded.Select(d => d.Path), cancellationToken);

            timer.Stop();
            LockOutput.PrintError(locks);
            if (head.Files.Count == 0)
            {
                AnsiConsole.MarkupLine($"Already up to date at [purple]#{head.HeadRevision}[/].");
                return 0;
            }

            AnsiConsole.MarkupLine($"Synced to [purple]#{head.HeadRevision}[/]: {downloaded.Count:N0} updated, {deletes.Count:N0} deleted.");
            AnsiConsole.MarkupLine($"[grey]Downloaded {bytes / (1024.0 * 1024):N1} MB in {timer.Elapsed.TotalSeconds:N2}s.[/]");
            if (conflicts.Count == 0) return 0;

            AnsiConsole.MarkupLine($"[yellow]{conflicts.Count:N0} file(s) changed both here and on the server were left as they are:[/]");
            foreach (string path in conflicts.Take(ListedConflicts)) AnsiConsole.MarkupLine($"[yellow]  {Markup.Escape(path)}[/]");
            if (conflicts.Count > ListedConflicts) AnsiConsole.MarkupLine($"[yellow]  ...and {conflicts.Count - ListedConflicts:N0} more[/]");
            AnsiConsole.MarkupLine($"[yellow]To take the server's version, run 'vx revert {Markup.Escape(CommandPath(workspace, conflicts[0]))}' (discards your change), then 'vx sync'.[/]");
            return 1;
        }

        /// <summary>
        /// A workspace path as the user would type it from the current folder.
        /// </summary>
        public static string CommandPath(VWorkspace workspace, string path)
        {
            string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), workspace.ToFullPath(path)).Replace(Path.DirectorySeparatorChar, '/');
            return relative.Contains(' ') ? $"\"{relative}\"" : relative;
        }
    }
}
