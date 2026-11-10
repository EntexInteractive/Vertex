// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public class BranchInterface(string url) : BaseInterface(url)
    {
        public async Task<ServerResult<VBranch>> CreateBranchAsync(Guid projectId, string branchName, Guid? parentId = null, CancellationToken cancellationToken = default)
        {
            string query = $"/api/v1/branches?projectId={projectId}&name={Uri.EscapeDataString(branchName)}";
            if (parentId.HasValue) query += $"&parentId={parentId.Value}";

            return await SendAsync<VBranch>(HttpMethod.Post, query, null, cancellationToken);
        }
    }
}