// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public class BranchInterface(string url, string? token = null) : BaseInterface(url, token)
    {
        public async Task<ServerResult<VBranch>> CreateBranchAsync(Guid projectId, string branchName, Guid? parentId = null, CancellationToken cancellationToken = default)
        {
            string query = $"/api/v1/branches?projectId={projectId}&name={Uri.EscapeDataString(branchName)}";
            if (parentId.HasValue) query += $"&parentId={parentId.Value}";

            return await SendAsync<VBranch>(HttpMethod.Post, query, null, cancellationToken);
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