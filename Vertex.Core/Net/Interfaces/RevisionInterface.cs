// Copyright 2026 Entex Interactive

using Vertex.Server;

namespace Vertex.Core.Net.Interfaces
{
    /// <summary>
    /// A file on a branch that changed since a revision. <see cref="FileId"/> is null when it was deleted.
    /// </summary>
    public sealed record HeadChange(string Path, string? FileId, long Revision);

    /// <summary>
    /// Every file that changed on a branch since a revision, and the branch's latest revision.
    /// </summary>
    public sealed record HeadChanges(long HeadRevision, IReadOnlyList<HeadChange> Files);

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
            SubmitRequest request = new()
            {
                ProjectId = projectId.ToString(),
                BranchId = branchId.ToString(),
                WorkspaceId = workspaceId.ToString(),
                Host = host,
                Message = message
            };

            request.Changes.AddRange(changes);
            SubmitResponse response = await RevisionsClient.SubmitAsync(request, cancellationToken: ct);
            return response.Revision;
        }

        /// <summary>
        /// Reads every page of files changed on a branch after <paramref name="afterRevision"/> (0 for all files).
        /// </summary>
        public async Task<HeadChanges> GetHeadChangesAsync(Guid projectId, Guid branchId, long afterRevision, CancellationToken ct = default)
        {
            List<HeadChange> files = new();
            GetHeadChangesRequest request = new() { ProjectId = projectId.ToString(), BranchId = branchId.ToString(), AfterRevision = afterRevision };
            long head;
            while (true)
            {
                GetHeadChangesResponse response = await RevisionsClient.GetHeadChangesAsync(request, cancellationToken: ct);
                head = response.HeadRevision;
                files.AddRange(response.Files.Select(f => new HeadChange(f.Path, f.FileId.Length == 0 ? null : f.FileId, f.Revision)));
                if (response.NextPageToken.Length == 0) break;
                request.PageToken = response.NextPageToken;
            }

            return new HeadChanges(head, files);
        }
    }
}
