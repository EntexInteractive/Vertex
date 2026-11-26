// Copyright 2026 Entex Interactive

using Vertex.Server;

namespace Vertex.Core.Net.Interfaces
{
    /// <summary>
    /// A file on a branch that changed since a revision. <see cref="FileId"/> is null when it was deleted.
    /// </summary>
    public sealed record HeadChange(string Path, string? FileId, long Revision);

    /// <summary>
    /// What a sync has to change: the files that differ between a revision and <see cref="HeadRevision"/>.
    /// </summary>
    /// <param name="HeadRevision">The revision synced to: the branch's latest, or the one asked for.</param>
    /// <param name="Files">The changed files, or with <paramref name="Full"/> every file at the revision.</param>
    /// <param name="Full">The list is every file on the branch at the revision, with no deletions: tracked files that
    /// aren't in it must be removed.</param>
    /// <param name="LatestRevision">The branch's latest revision.</param>
    public sealed record HeadChanges(long HeadRevision, IReadOnlyList<HeadChange> Files, bool Full = false, long LatestRevision = 0);

    /// <summary>
    /// One revision in a branch's history.
    /// </summary>
    /// <param name="DisplayName">The submitter's display name, or null when they have none.</param>
    /// <param name="Files">How many files the revision changed.</param>
    public sealed record RevisionSummary(long Number, string Message, string Username, string? DisplayName, DateTimeOffset CreatedAt, int Files);

    /// <summary>
    /// A file a revision changed. <see cref="FileId"/> is null when the revision deleted it.
    /// </summary>
    public sealed record RevisionChange(string Path, string? FileId);

    /// <summary>
    /// Project history: submitting revisions and syncing the branch head.
    /// </summary>
    public class RevisionInterface : BaseInterface
    {
        public Revisions.RevisionsClient RevisionsClient { get; }

        public RevisionInterface(string url, string? token = null) : base(url, token)
        {
            RevisionsClient = new Revisions.RevisionsClient(GrpcChannel);
        }

        /// <summary>
        /// Submits changes as one revision. Their content must already be uploaded with
        /// <see cref="FilesInterface.UploadAsync"/>.
        /// </summary>
        /// <returns>The new revision number.</returns>
        /// <param name="host">The workspace's machine name, which lock messages show.</param>
        public async Task<long> SubmitAsync(Guid projectId, Guid branchId, Guid workspaceId, string host, string message, IEnumerable<FileChange> changes, CancellationToken ct = default)
        {
            return (await SubmitRevisionAsync(projectId, branchId, workspaceId, host, message, changes, ct)).Revision;
        }

        /// <summary>
        /// The same as <see cref="SubmitAsync"/>, with the full response: the new revision and, for a branch with a
        /// merge target, what its merge chain did (<see cref="SubmitResponse.AutoMerges"/>).
        /// </summary>
        public async Task<SubmitResponse> SubmitRevisionAsync(Guid projectId, Guid branchId, Guid workspaceId, string host, string message, IEnumerable<FileChange> changes,
            CancellationToken ct = default)
        {
            SubmitRequest request = new()
            {
                ProjectId = projectId.ToString(),
                BranchId = branchId.ToString(),
                WorkspaceId = workspaceId.ToString(),
                Host = host,
                Message = message
            };

            request.Changes.AddRange(changes);
            return await RevisionsClient.SubmitAsync(request, cancellationToken: ct);
        }

        /// <summary>
        /// Merges every change made on <paramref name="sourceBranchId"/> since the last merge between the two into
        /// <paramref name="targetBranchId"/>, as one revision. The branches must be a branch and its parent.
        /// </summary>
        /// <returns>The revision written (0 when the target already had everything), or the conflicts that refused
        /// the merge.</returns>
        /// <param name="projectId">The target's project.</param>
        /// <param name="message">The revision's description; empty for "Merge source into target".</param>
        /// <param name="sourceProjectId">The source's project, when one branch is the other's upstream in another project.</param>
        public async Task<MergeResponse> MergeAsync(Guid projectId, Guid sourceBranchId, Guid targetBranchId, string message, MergeResolution resolve = MergeResolution.None,
            Guid? workspaceId = null, string? host = null, CancellationToken ct = default, Guid? sourceProjectId = null)
        {
            MergeRequest request = new()
            {
                ProjectId = projectId.ToString(),
                SourceBranchId = sourceBranchId.ToString(),
                TargetBranchId = targetBranchId.ToString(),
                Message = message,
                Resolve = resolve,
                WorkspaceId = workspaceId?.ToString() ?? string.Empty,
                Host = host ?? string.Empty,
                SourceProjectId = sourceProjectId is Guid other && other != projectId ? other.ToString() : string.Empty
            };

            return await RevisionsClient.MergeAsync(request, cancellationToken: ct);
        }

        /// <summary>
        /// Reads every page of what a sync from <paramref name="afterRevision"/> (0 for a workspace with nothing yet)
        /// to <paramref name="revision"/> (0 for the latest) has to change.
        /// </summary>
        /// <exception cref="Grpc.Core.RpcException"><c>FailedPrecondition</c>: the revision is newer than the branch's
        /// latest (the status detail says so in one line).</exception>
        public async Task<HeadChanges> GetHeadChangesAsync(Guid projectId, Guid branchId, long afterRevision, CancellationToken ct = default)
        {
            return await GetHeadChangesAsync(projectId, branchId, afterRevision, 0, ct);
        }

        /// <inheritdoc cref="GetHeadChangesAsync(Guid, Guid, long, CancellationToken)"/>
        public async Task<HeadChanges> GetHeadChangesAsync(Guid projectId, Guid branchId, long afterRevision, long revision, CancellationToken ct = default)
        {
            List<HeadChange> files = new();
            GetHeadChangesRequest request = new() { ProjectId = projectId.ToString(), BranchId = branchId.ToString(), AfterRevision = afterRevision, Revision = revision };
            long head, latest;
            bool full;
            while (true)
            {
                GetHeadChangesResponse response = await RevisionsClient.GetHeadChangesAsync(request, cancellationToken: ct);
                head = response.HeadRevision;
                latest = response.LatestRevision;
                full = response.Full;
                files.AddRange(response.Files.Select(f => new HeadChange(f.Path, f.FileId.Length == 0 ? null : f.FileId, f.Revision)));
                if (response.NextPageToken.Length == 0) break;
                request.PageToken = response.NextPageToken;
            }

            // Servers before targeted sync leave the new fields unset; their list from revision 0 is every file.
            return new HeadChanges(head, files, full || afterRevision == 0, latest == 0 ? head : latest);
        }

        /// <summary>
        /// Up to <paramref name="limit"/> revisions on a branch after <paramref name="afterRevision"/> (0 for the first),
        /// oldest first. Fewer than the limit means the list reached the branch's latest revision.
        /// </summary>
        /// <param name="limit">At most 1,000.</param>
        public async Task<IReadOnlyList<RevisionSummary>> GetRevisionsAsync(Guid projectId, Guid branchId, long afterRevision, int limit = 100, CancellationToken ct = default)
        {
            GetRevisionsRequest request = new() { ProjectId = projectId.ToString(), BranchId = branchId.ToString(), AfterRevision = afterRevision, Limit = limit };
            GetRevisionsResponse response = await RevisionsClient.GetRevisionsAsync(request, cancellationToken: ct);
            return response.Revisions
                .Select(r => new RevisionSummary(r.Number, r.Message, r.Username, r.DisplayName.Length == 0 ? null : r.DisplayName, DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAt), r.Files))
                .ToList();
        }

        /// <summary>
        /// Reads every page of the files one revision on a branch changed.
        /// </summary>
        public async Task<IReadOnlyList<RevisionChange>> GetRevisionChangesAsync(Guid projectId, Guid branchId, long revision, CancellationToken ct = default)
        {
            List<RevisionChange> changes = new();
            GetRevisionChangesRequest request = new() { ProjectId = projectId.ToString(), BranchId = branchId.ToString(), Revision = revision };
            while (true)
            {
                GetRevisionChangesResponse response = await RevisionsClient.GetRevisionChangesAsync(request, cancellationToken: ct);
                changes.AddRange(response.Changes.Select(c => new RevisionChange(c.Path, c.FileId.Length == 0 ? null : c.FileId)));
                if (response.NextPageToken.Length == 0) break;
                request.PageToken = response.NextPageToken;
            }

            return changes;
        }
    }
}
