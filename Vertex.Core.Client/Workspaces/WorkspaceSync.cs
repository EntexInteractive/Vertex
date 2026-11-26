// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using Vertex.Core.Client.IO;
using Vertex.Core.Data;
using Vertex.Core.Net;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// What a sync did.
    /// </summary>
    /// <param name="Head">What the server listed, including the revision synced to and the branch's latest.</param>
    /// <param name="Conflicts">Files changed both here and on the server, left as they are.</param>
    /// <param name="Bytes">Compressed bytes downloaded.</param>
    public sealed record SyncOutcome(HeadChanges Head, int Updated, int Deleted, IReadOnlyList<string> Conflicts, long Bytes, LockReport Locks)
    {
        public bool Changed => Updated > 0 || Deleted > 0;
    }

    /// <summary>
    /// Brings a workspace's files to a revision of its branch, for sync, checkout and clone. Files changed locally are
    /// never overwritten; they come back as conflicts.
    /// </summary>
    public static class WorkspaceSync
    {
        /// <summary>
        /// Scans the workspace, then syncs it to <paramref name="revision"/> (0 for the latest).
        /// </summary>
        /// <exception cref="InvalidOperationException">The server is too old to sync to an older revision.</exception>
        public static async Task<SyncOutcome> RunAsync(LinkedWorkspace linked, WorkspaceConnection connection, long revision, IOperationReporter reporter,
            CancellationToken cancellationToken)
        {
            using WorkspaceIndex index = linked.Workspace.OpenIndex();

            // Scan first so local edits are known and never overwritten.
            WorkspaceStatus status = await reporter.ScanAsync("Scanning...", linked.Workspace, index, new ScanOptions { MaxConcurrency = connection.Parallel }, cancellationToken);
            return await RunAsync(linked, connection, index, status, revision, reporter, cancellationToken);
        }

        /// <summary>
        /// Syncs from the index's synced revision to <paramref name="revision"/> (0 for the latest).
        /// </summary>
        /// <param name="status">A scan of the workspace made just before, so local edits are known.</param>
        /// <exception cref="InvalidOperationException">The server is too old to sync to an older revision.</exception>
        public static async Task<SyncOutcome> RunAsync(LinkedWorkspace linked, WorkspaceConnection connection, WorkspaceIndex index, WorkspaceStatus status, long revision,
            IOperationReporter reporter, CancellationToken cancellationToken)
        {
            VWorkspace workspace = linked.Workspace;
            int parallel = connection.Parallel;
            long.TryParse(index.GetMeta(WorkspaceIndex.SyncedRevisionKey), out long synced);

            HeadChanges head;
            using (RevisionInterface revisions = new(connection.ServerAddress, connection.Token))
            {
                head = await reporter.StepAsync("Checking server...", () => revisions.GetHeadChangesAsync(linked.ProjectId, linked.BranchId, synced, revision, cancellationToken));
            }

            // A server from before targeted sync ignores the revision and answers with the latest.
            if (revision != 0 && head.HeadRevision != revision)
                throw new InvalidOperationException($"This server can only sync to the latest revision (#{head.HeadRevision}). Update the server to sync to #{revision}.");

            Dictionary<string, WorkspaceChange> changedLocally = new(WorkspaceScanner.PathComparer);
            foreach (WorkspaceChange change in status.Changes)
            {
                changedLocally[change.Path] = change;
                if (change.PreviousPath is not null) changedLocally[change.PreviousPath] = change;
            }

            List<HeadChange> downloads = new();
            List<string> conflicts = new();
            List<IndexEntry> upserts = new();
            List<string> deletes = new();
            HashSet<string> listed = new(WorkspaceScanner.PathComparer);
            foreach (HeadChange file in head.Files)
            {
                listed.Add(file.Path);
                status.Entries.TryGetValue(file.Path, out IndexEntry? entry);

                // Already matches the server, e.g. the same edit made here or a file both branches share: only the
                // base moves.
                if (file.FileId is not null && entry?.FileId == file.FileId)
                {
                    if (entry.BaseId != file.FileId) upserts.Add(entry with { BaseId = file.FileId });
                    continue;
                }

                if (changedLocally.TryGetValue(file.Path, out WorkspaceChange? local))
                {
                    // Deleted here and gone at the target too: nothing to keep.
                    if (file.FileId is null && local.Type == ChangeType.Deleted && entry is not null) deletes.Add(entry.Path);
                    else conflicts.Add(file.Path);
                }
                else if (file.FileId is null)
                {
                    if (entry is not null) deletes.Add(Delete(workspace, entry));
                }
                else
                {
                    downloads.Add(file);
                }
            }

            // A full list names every file at the target, so tracked files missing from it don't exist there.
            if (head.Full)
            {
                foreach (IndexEntry entry in status.Entries.Values)
                {
                    if (entry.BaseId is null || listed.Contains(entry.Path)) continue;
                    if (changedLocally.TryGetValue(entry.Path, out WorkspaceChange? local))
                    {
                        if (local.Type == ChangeType.Deleted) deletes.Add(entry.Path);
                        else conflicts.Add(entry.Path);
                    }
                    else
                    {
                        deletes.Add(Delete(workspace, entry));
                    }
                }
            }

            ConcurrentBag<IndexEntry> downloaded = new();
            long bytes = 0;
            try
            {
                using FilesInterface files = new(connection.ServerAddress, connection.Token) { MaxConcurrency = 2 };
                if (downloads.Count > 0)
                {
                    TransferProgress transfer = new();
                    OperationPhase phase = new("Syncing", PhaseUnit.Files) { Detail = $"{downloads.Count:N0} file(s)", Transfer = transfer };
                    phase.SetTotal(downloads.Count);
                    await reporter.ProgressAsync([phase], () => Parallel.ForEachAsync(downloads,
                        new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (file, ct) =>
                        {
                            string fullPath = workspace.ToFullPath(file.Path);
                            DownloadResult result = await files.DownloadAsync(file.FileId!, fullPath, ct, transfer);
                            Interlocked.Add(ref bytes, result.CompressedBytesDownloaded);

                            FileInfo info = new(fullPath);
                            downloaded.Add(new IndexEntry(status.Entries.TryGetValue(file.Path, out IndexEntry? entry) ? entry.Path : file.Path, info.Length, info.LastWriteTimeUtc.Ticks,
                                file.FileId!, file.FileId, DateTime.UtcNow.Ticks));
                            phase.Add(1);
                        }));
                }
            }
            finally
            {
                // Record whatever arrived, even when a download failed part way.
                index.Apply(upserts.Concat(downloaded), deletes);
            }

            // Skipped conflicts must come back on the next sync, so only a clean sync moves the synced revision. After a
            // full list (e.g. back to an older revision) the other files already moved, so the old number would be
            // wrong too: 0 makes the next sync compare every file again.
            if (conflicts.Count == 0) index.SetMeta(WorkspaceIndex.SyncedRevisionKey, head.HeadRevision.ToString());
            else if (head.Full) index.SetMeta(WorkspaceIndex.SyncedRevisionKey, "0");

            // Replacing a file drops its read-only flag, so check the files just written against everyone's locks.
            using LockInterface lockClient = new(connection.ServerAddress, connection.Token);
            LockReport locks = await connection.OpenLocks(linked, index, lockClient).ReconcileAsync(status.Changes, downloaded.Select(d => d.Path), cancellationToken);

            return new SyncOutcome(head, downloaded.Count, deletes.Count, conflicts, bytes, locks);
        }

        private static string Delete(VWorkspace workspace, IndexEntry entry)
        {
            string fullPath = workspace.ToFullPath(entry.Path);
            if (File.Exists(fullPath))
            {
                FileSystemUtility.MakeWritable(fullPath);
                File.Delete(fullPath);
                RemoveEmptyFolders(workspace, Path.GetDirectoryName(fullPath));
            }

            return entry.Path;
        }

        /// <summary>
        /// Removes <paramref name="folder"/> and its parents up to the workspace root while they're empty, so files
        /// the target doesn't have don't leave their folders behind.
        /// </summary>
        private static void RemoveEmptyFolders(VWorkspace workspace, string? folder)
        {
            string root = Path.TrimEndingDirectorySeparator(workspace.Root);
            while (folder is not null && folder.Length > root.Length && !WorkspaceScanner.PathComparer.Equals(folder, root))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(folder).Any()) return;
                    Directory.Delete(folder);
                }
                catch (IOException)
                {
                    return; // In use or already gone: leave it.
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }

                folder = Path.GetDirectoryName(folder);
            }
        }
    }
}
