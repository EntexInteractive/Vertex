// Copyright 2026 Entex Interactive

using Vertex.Core.Client.IO;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// What a revert did.
    /// </summary>
    /// <param name="Reverted">Files restored to their last submitted or synced content.</param>
    /// <param name="LeftAdded">New files that matched but were left alone, because the server has no earlier version.</param>
    /// <param name="Locks">The lock state afterwards, or null when nothing was reverted.</param>
    public sealed record RevertOutcome(int Reverted, IReadOnlyList<string> LeftAdded, LockReport? Locks)
    {
        public bool NothingToRevert => Reverted == 0 && LeftAdded.Count == 0;
    }

    /// <summary>
    /// Discards local changes: modified and deleted files get the content they had at the last submit or sync back
    /// from the server, and moved files go back to where they were. New files are left alone, because the server has
    /// no copy to restore.
    /// </summary>
    public static class WorkspaceRevert
    {
        public static async Task<RevertOutcome> RunAsync(LinkedWorkspace linked, WorkspaceConnection connection, PathFilter filter, IOperationReporter reporter,
            CancellationToken cancellationToken)
        {
            VWorkspace workspace = linked.Workspace;
            int parallel = connection.Parallel;
            using WorkspaceIndex index = workspace.OpenIndex();
            WorkspaceStatus status = await reporter.ScanAsync("Scanning for changes...", workspace, index, new ScanOptions { MaxConcurrency = parallel }, cancellationToken);

            List<WorkspaceChange> selected = status.Changes.Where(filter.Matches).ToList();
            List<string> added = selected.Where(c => c.Type == ChangeType.Created).Select(c => c.Path).ToList();
            List<WorkspaceChange> restore = selected.Where(c => c.Type != ChangeType.Created).ToList();
            if (restore.Count == 0) return new RevertOutcome(0, added, null);

            // Each restored file goes back to its base content, at its old path for a move.
            List<(string Path, string FileId)> downloads = restore.Select(c => (c.PreviousPath ?? c.Path, c.Type == ChangeType.Modified ? status.Entries[c.Path].BaseId! : c.FileId)).ToList();
            List<IndexEntry> upserts = new();
            List<string> deletes = new();
            using (FilesInterface files = new(connection.ServerAddress, connection.Token) { MaxConcurrency = 2 })
            {
                object gate = new();
                await reporter.StepAsync("Reverting...", async () =>
                {
                    await Parallel.ForEachAsync(downloads, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (file, ct) =>
                    {
                        string fullPath = workspace.ToFullPath(file.Path);
                        await files.DownloadAsync(file.FileId, fullPath, ct);
                        FileInfo info = new(fullPath);
                        lock (gate) upserts.Add(new IndexEntry(file.Path, info.Length, info.LastWriteTimeUtc.Ticks, file.FileId, file.FileId, DateTime.UtcNow.Ticks));
                    });
                    return true;
                });
            }

            // The new path of a move holds the same content that was just restored at the old path, so removing it
            // loses nothing.
            foreach (WorkspaceChange move in restore.Where(c => c.Type == ChangeType.Renamed))
            {
                string fullPath = workspace.ToFullPath(move.Path);
                FileSystemUtility.MakeWritable(fullPath);
                File.Delete(fullPath);
                deletes.Add(move.Path);
            }

            index.Apply(upserts, deletes);

            // Reverted files are no longer changed, so their locks are released; restored files someone else holds
            // become read-only again.
            HashSet<WorkspaceChange> reverted = restore.ToHashSet();
            using LockInterface lockClient = new(connection.ServerAddress, connection.Token);
            LockReport locks = await connection.OpenLocks(linked, index, lockClient)
                .ReconcileAsync(status.Changes.Where(c => !reverted.Contains(c)).ToList(), downloads.Select(d => d.Path), cancellationToken);

            return new RevertOutcome(restore.Count, added, locks);
        }
    }
}
