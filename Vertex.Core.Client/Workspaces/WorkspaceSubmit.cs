// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Server;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// How a submit ended.
    /// </summary>
    public enum SubmitResult
    {
        /// <summary>A revision was written.</summary>
        Submitted,

        /// <summary>No changes matched; nothing was sent.</summary>
        NothingToSubmit,

        /// <summary>A change is locked by someone else; nothing was sent.</summary>
        Blocked
    }

    /// <summary>
    /// What a submit did.
    /// </summary>
    public sealed record SubmitOutcome
    {
        public required SubmitResult Result { get; init; }

        /// <summary>The revision written, or 0.</summary>
        public long Revision { get; init; }

        /// <summary>Files in the revision.</summary>
        public int Files { get; init; }

        /// <summary>Matching files that couldn't be read and were left out.</summary>
        public IReadOnlyList<string> Unreadable { get; init; } = [];

        /// <summary>Matching changes locked by someone else, when <see cref="Result"/> is <see cref="SubmitResult.Blocked"/>.</summary>
        public IReadOnlyList<HeldLock> Blocked { get; init; } = [];

        /// <summary>What was uploaded, or null when the server had every file already.</summary>
        public UploadResult? Upload { get; init; }

        /// <summary>The server's answer, with the branch's automatic merges.</summary>
        public SubmitResponse? Response { get; init; }

        /// <summary>The lock state afterwards, or null when nothing was submitted.</summary>
        public LockReport? Locks { get; init; }
    }

    /// <summary>
    /// Submits a workspace's changes as one revision: every added, modified, deleted and moved file the filter
    /// matches. Content the server already stores is not sent again.
    /// </summary>
    public static class WorkspaceSubmit
    {
        /// <exception cref="ArgumentException">The message is empty.</exception>
        public static async Task<SubmitOutcome> RunAsync(LinkedWorkspace linked, WorkspaceConnection connection, PathFilter filter, string message, IOperationReporter reporter,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Describe the revision before submitting.", nameof(message));

            int parallel = connection.Parallel;
            using WorkspaceIndex index = linked.Workspace.OpenIndex();
            WorkspaceStatus status = await reporter.ScanAsync("Scanning...", linked.Workspace, index, new ScanOptions { MaxConcurrency = parallel, KeepChunks = true },
                cancellationToken);

            List<WorkspaceChange> selected = status.Changes.Where(filter.Matches).ToList();
            List<string> unreadable = status.Unreadable.Where(filter.Matches).ToList();
            if (selected.Count == 0) return new SubmitOutcome { Result = SubmitResult.NothingToSubmit, Unreadable = unreadable };

            // Lock every change before uploading anything, so a file someone else holds stops the submit right away.
            // The server checks again when the revision is written.
            using LockInterface lockClient = new(connection.ServerAddress, connection.Token);
            WorkspaceLocks locks = connection.OpenLocks(linked, index, lockClient);
            LockReport lockReport = await locks.ReconcileAsync(status.Changes, ct: cancellationToken);
            List<HeldLock> blocked = lockReport.HeldByOthers.Where(h => filter.Matches(h.Path)).ToList();
            if (blocked.Count > 0) return new SubmitOutcome { Result = SubmitResult.Blocked, Blocked = blocked, Unreadable = unreadable, Locks = lockReport };

            // Upload content the server doesn't have yet. Files hashed by this scan already have their chunk lists;
            // others are chunked now, and only when the server is missing them.
            using FilesInterface files = new(connection.ServerAddress, connection.Token) { MaxConcurrency = parallel };
            List<WorkspaceChange> withContent = selected.Where(c => c.Type != ChangeType.Deleted).ToList();
            HashSet<string> missing = await reporter.StepAsync("Checking server...", () => files.FindMissingFilesAsync(withContent.Select(c => c.FileId), cancellationToken));

            UploadResult? upload = null;
            List<WorkspaceChange> toSend = withContent.Where(c => missing.Contains(c.FileId)).DistinctBy(c => c.FileId).ToList();
            if (toSend.Count > 0)
            {
                // Files the scan didn't hash (their stats hadn't changed since an earlier scan) are chunked now.
                List<WorkspaceChange> unchunked = toSend.Where(c => c.Chunks is null).ToList();
                OperationPhase? hashing = unchunked.Count > 0 ? new OperationPhase("Hashing", PhaseUnit.Bytes) : null;
                hashing?.SetTotal(Math.Max(1, unchunked.Sum(c => status.Entries[c.Path].Size)));
                TransferProgress transfer = new();
                OperationPhase uploading = new("Submitting", PhaseUnit.Bytes, started: false) { Transfer = transfer };
                List<OperationPhase> phases = hashing is null ? [uploading] : [hashing, uploading];

                await reporter.ProgressAsync(phases, async () =>
                {
                    ChunkedFile[] chunked = await Task.Run(() => toSend.AsParallel().WithDegreeOfParallelism(parallel).WithCancellation(cancellationToken).Select(c =>
                    {
                        ChunkedFile file = Chunk(linked.Workspace, c, cancellationToken);
                        if (c.Chunks is null) hashing?.Add(status.Entries[c.Path].Size);
                        return file;
                    }).ToArray(), cancellationToken);

                    hashing?.Complete();
                    uploading.Start();
                    upload = await files.UploadAsync(chunked, ct: cancellationToken, progress: transfer);
                });
            }

            List<FileChange> changes = new();
            foreach (WorkspaceChange change in selected)
            {
                switch (change.Type)
                {
                    case ChangeType.Created:
                        changes.Add(new FileChange { Path = change.Path, FileId = change.FileId });
                        break;
                    case ChangeType.Modified:
                        changes.Add(new FileChange { Path = change.Path, FileId = change.FileId, BaseFileId = status.Entries[change.Path].BaseId! });
                        break;
                    case ChangeType.Deleted:
                        changes.Add(new FileChange { Path = change.Path, BaseFileId = change.FileId });
                        break;
                    case ChangeType.Renamed:
                        // The server stores a move as a deletion and a creation; the content is already there.
                        changes.Add(new FileChange { Path = change.PreviousPath!, BaseFileId = change.FileId });
                        changes.Add(new FileChange { Path = change.Path, FileId = change.FileId });
                        break;
                }
            }

            using RevisionInterface revisions = new(connection.ServerAddress, connection.Token);
            SubmitResponse response = await revisions.SubmitRevisionAsync(linked.ProjectId, linked.BranchId, linked.WorkspaceId, WorkspaceLocks.Host, message.Trim(), changes,
                cancellationToken);
            long revision = response.Revision;

            // The submitted content is now the base the next changes are measured against.
            List<IndexEntry> upserts = new();
            List<string> deletes = new();
            foreach (WorkspaceChange change in selected)
            {
                if (change.Type == ChangeType.Deleted)
                {
                    deletes.Add(change.Path);
                    continue;
                }

                if (change.Type == ChangeType.Renamed) deletes.Add(change.PreviousPath!);
                upserts.Add(status.Entries[change.Path] with { BaseId = change.FileId });
            }

            index.Apply(upserts, deletes);

            // When nobody else submitted in between, the workspace is still in step with the branch.
            long.TryParse(index.GetMeta(WorkspaceIndex.SyncedRevisionKey), out long synced);
            if (synced == revision - 1) index.SetMeta(WorkspaceIndex.SyncedRevisionKey, revision.ToString());

            // The server released the submitted files' locks; keep the rest locked and catch up with everyone else's.
            HashSet<WorkspaceChange> submitted = selected.ToHashSet();
            LockReport after = await locks.ReconcileAsync(status.Changes.Where(c => !submitted.Contains(c)).ToList(), ct: cancellationToken);

            return new SubmitOutcome
            {
                Result = SubmitResult.Submitted,
                Revision = revision,
                Files = selected.Count,
                Unreadable = unreadable,
                Upload = upload,
                Response = response,
                Locks = after
            };
        }

        private static ChunkedFile Chunk(VWorkspace workspace, WorkspaceChange change, CancellationToken cancellationToken)
        {
            ChunkedFile chunked = change.Chunks ?? ContentChunker.ChunkFile(workspace.ToFullPath(change.Path), cancellationToken);
            if (chunked.FileId != change.FileId) throw new IOException($"{change.Path} changed while it was being submitted. Submit again.");
            return chunked;
        }
    }
}
