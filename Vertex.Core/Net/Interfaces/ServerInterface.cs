// Copyright 2026 Entex Interactive

using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    /// <summary>
    /// Methods for Vertex server endpoints.
    /// </summary>
    public sealed class ServerInterface(string url, string? token = null) : BaseInterface(url, token)
    {
        public Task<ServerResult<InfoResponse>> GetInfoAsync(CancellationToken ct = default)
        {
            return SendAsync<InfoResponse>(HttpMethod.Get, "api/v1/server/info", cancellationToken: ct);
        }
    }
}