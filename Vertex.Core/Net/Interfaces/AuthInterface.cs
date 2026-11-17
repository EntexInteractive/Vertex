// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Requests;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public sealed class AuthInterface(string url, string? token = null) : BaseInterface(url, token)
    {
        /// <summary>
        /// Exchanges a username and password for a token. Wrong credentials come back as a 401 result, not an
        /// exception.
        /// </summary>
        public Task<ServerResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
        {
            return SendAsync<LoginResponse>(HttpMethod.Post, "api/v1/auth/login", request, false, ct);
        }

        /// <summary>
        /// Revokes the token this interface was created with.
        /// </summary>
        public Task<ServerResult<bool>> LogoutAsync(CancellationToken ct = default)
        {
            return SendAsync<bool>(HttpMethod.Delete, "api/v1/auth/token", null, false, ct);
        }

        /// <summary>
        /// The user the token belongs to.
        /// </summary>
        public Task<ServerResult<VUser>> GetCurrentUserAsync(CancellationToken ct = default)
        {
            return SendAsync<VUser>(HttpMethod.Get, "api/v1/auth/me", cancellationToken: ct);
        }
    }
}
