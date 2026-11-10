// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public class BranchInterface(string url) : BaseInterface(url)
    {
        public async Task<ServerResult<VBranch>> CreateBranchAsync(Guid projectId, string branchName, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VBranch>(HttpMethod.Post, "/api/v1/branches", new { projectId, name = branchName }, cancellationToken);
        }
    }
}