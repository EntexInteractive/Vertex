// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Requests;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public sealed class UsersInterface(string url, string? token = null) : BaseInterface(url, token)
    {
        public Task<ServerResult<IReadOnlyList<VUser>>> ListUsersAsync(CancellationToken ct = default)
        {
            return SendAsync<IReadOnlyList<VUser>>(HttpMethod.Get, "api/v1/users", cancellationToken: ct);
        }

        public Task<ServerResult<VUser>> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
        {
            return SendAsync<VUser>(HttpMethod.Post, "api/v1/users", request, ct);
        }

        public Task<ServerResult<VUser>> UpdateUserAsync(string username, UpdateUserRequest request, CancellationToken ct = default)
        {
            return SendAsync<VUser>(HttpMethod.Patch, $"api/v1/users?username={Uri.EscapeDataString(username)}", request, ct);
        }

        public Task<ServerResult<bool>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
        {
            return SendAsync<bool>(HttpMethod.Put, "api/v1/users/me/password", request, ct);
        }
    }
}
