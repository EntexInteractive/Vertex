// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Requests;
using Vertex.Core.Net.Results;
using Vertex.Core.Security;

namespace Vertex.Core.Net.Interfaces
{
    public sealed class AccessInterface(string url, string? token = null) : BaseInterface(url, token)
    {
        public Task<ServerResult<IReadOnlyList<VAccessGrant>>> ListGrantsAsync(Guid projectId, CancellationToken ct = default)
        {
            return SendAsync<IReadOnlyList<VAccessGrant>>(HttpMethod.Get, $"api/v1/access?projectId={projectId}", cancellationToken: ct);
        }

        public Task<ServerResult<VAccessGrant>> GrantAsync(Guid projectId, string name, AccessLevel level, CancellationToken ct = default)
        {
            return SendAsync<VAccessGrant>(HttpMethod.Put, $"api/v1/access?projectId={projectId}", new GrantAccessRequest(name, level), ct);
        }

        public Task<ServerResult<bool>> RevokeAsync(Guid projectId, string name, CancellationToken ct = default)
        {
            return SendAsync<bool>(HttpMethod.Delete, $"api/v1/access?projectId={projectId}&name={Uri.EscapeDataString(name)}", cancellationToken: ct);
        }

        public Task<ServerResult<VProject>> SetVisibilityAsync(Guid projectId, ProjectVisibility visibility, CancellationToken ct = default)
        {
            return SendAsync<VProject>(HttpMethod.Put, $"api/v1/access/visibility?projectId={projectId}&visibility={visibility}", cancellationToken: ct);
        }
    }
}
