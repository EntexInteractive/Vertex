// Copyright 2026 Entex Interactive

using Vertex.Server;

namespace Vertex.Core.Net.Interfaces
{
    /// <summary>
    /// File locks on a branch: locking and releasing paths, and the event feed of everyone's locks.
    /// </summary>
    public class LockInterface : BaseInterface
    {
        public Locks.LocksClient LocksClient { get; }

        public LockInterface(string url, string? token = null) : base(url, token)
        {
            LocksClient = new Locks.LocksClient(GrpcChannel);
        }

        /// <summary>
        /// Locks paths for this workspace, batching large lists.
        /// </summary>
        /// <returns>One result per path, in order.</returns>
        public async Task<List<LockResult>> LockAsync(Guid projectId, Guid branchId, Guid workspaceId, string host, IReadOnlyCollection<string> paths, CancellationToken ct = default)
        {
            List<LockResult> results = new(paths.Count);
            foreach (string[] batch in paths.Chunk(FileTransferLimits.MaximumPathsPerLockRequest))
            {
                LockRequest request = new() { ProjectId = projectId.ToString(), BranchId = branchId.ToString(), WorkspaceId = workspaceId.ToString(), Host = host };
                request.Paths.AddRange(batch);
                LockResponse response = await LocksClient.LockAsync(request, cancellationToken: ct);
                results.AddRange(response.Results);
            }

            return results;
        }

        /// <summary>
        /// Releases this workspace's locks on paths; with <paramref name="force"/>, also locks held elsewhere that
        /// the caller may release.
        /// </summary>
        /// <returns>The paths released.</returns>
        public async Task<List<string>> UnlockAsync(Guid projectId, Guid branchId, Guid workspaceId, IReadOnlyCollection<string> paths, bool force = false, CancellationToken ct = default)
        {
            List<string> released = new();
            foreach (string[] batch in paths.Chunk(FileTransferLimits.MaximumPathsPerLockRequest))
            {
                UnlockRequest request = new() { ProjectId = projectId.ToString(), BranchId = branchId.ToString(), WorkspaceId = workspaceId.ToString(), Force = force };
                request.Paths.AddRange(batch);
                UnlockResponse response = await LocksClient.UnlockAsync(request, cancellationToken: ct);
                released.AddRange(response.Released);
            }

            return released;
        }

        /// <summary>
        /// The branch's lock changes after <paramref name="afterSeq"/> (0 for the full list).
        /// </summary>
        public async Task<GetLockEventsResponse> GetLockEventsAsync(Guid projectId, Guid branchId, long afterSeq, CancellationToken ct = default)
        {
            return await LocksClient.GetLockEventsAsync(new GetLockEventsRequest { ProjectId = projectId.ToString(), BranchId = branchId.ToString(), AfterSeq = afterSeq }, cancellationToken: ct);
        }
    }
}
