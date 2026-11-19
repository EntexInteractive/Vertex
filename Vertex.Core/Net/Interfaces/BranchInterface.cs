// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public class BranchInterface(string url, string? token = null) : BaseInterface(url, token)
    {
        /// <summary>
        /// Creates a branch: a copy of <paramref name="parentId"/>'s files, or empty without one. With
        /// <paramref name="mergeToId"/>, every revision on it is merged on into that branch.
        /// </summary>
        public async Task<ServerResult<VBranch>> CreateBranchAsync(Guid projectId, string branchName, Guid? parentId = null, CancellationToken cancellationToken = default,
            Guid? mergeToId = null)
        {
            string query = $"/api/v1/branches?projectId={projectId}&name={Uri.EscapeDataString(branchName)}";
            if (parentId.HasValue) query += $"&parentId={parentId.Value}";
            if (mergeToId.HasValue) query += $"&mergeToId={mergeToId.Value}";

            return await SendAsync<VBranch>(HttpMethod.Post, query, null, cancellationToken);
        }

        /// <summary>
        /// Sets the branch <paramref name="branchId"/>'s revisions are merged into automatically, or with null stops
        /// it. Fails with 400 (and a one-line reason) for a target that's missing, unrelated or would make a loop.
        /// </summary>
        public async Task<ServerResult<MergeToChange>> SetMergeToAsync(Guid projectId, Guid branchId, Guid? mergeToId, CancellationToken cancellationToken = default)
        {
            string query = $"/api/v1/branches/merge-to?projectId={projectId}&branchId={branchId}";
            if (mergeToId.HasValue) query += $"&mergeToId={mergeToId.Value}";
            return await SendAsync<MergeToChange>(HttpMethod.Put, query, null, cancellationToken);
        }

        public async Task<ServerResult<VBranch>> GetBranchByIdAsync(Guid projectId, Guid branchId, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VBranch>(HttpMethod.Get, $"/api/v1/branches/by-id?projectId={projectId}&branchId={branchId}", null, cancellationToken);
        }

        /// <summary>
        /// Deletes a branch by name. Fails with 404 when it doesn't exist and 409 when other branches were
        /// created from it.
        /// </summary>
        public async Task<ServerResult<VBranch>> DeleteBranchAsync(Guid projectId, string branchName, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VBranch>(HttpMethod.Delete, $"/api/v1/branches?projectId={projectId}&name={Uri.EscapeDataString(branchName)}", null, cancellationToken);
        }

        /// <summary>
        /// Lists a project's branches, ordered by name.
        /// </summary>
        public async Task<ServerResult<IReadOnlyList<VBranch>>> ListBranchesAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            return await SendAsync<IReadOnlyList<VBranch>>(HttpMethod.Get, $"/api/v1/branches?projectId={projectId}", null, cancellationToken);
        }

        public async Task<ServerResult<VBranch>> GetBranchByNameAsync(Guid projectId, string branchName, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VBranch>(HttpMethod.Get, $"/api/v1/branches/by-name?projectId={projectId}&name={Uri.EscapeDataString(branchName)}", null, cancellationToken);
        }
    }
}