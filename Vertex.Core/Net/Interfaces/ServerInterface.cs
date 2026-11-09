// Copyright 2026 Entex Interactive

using Vertex.Net.Responses;
using Vertex.Net.Results;

namespace Vertex.Net.Interfaces
{
    /// <summary>
    /// Methods for Vertex server endpoints.
    /// </summary>
    public sealed class ServerInterface(string url) : BaseInterface(url)
    {
        public Task<ServerResult<InfoResponse>> GetInfoAsync(CancellationToken ct = default)
        {
            return SendAsync<InfoResponse>(HttpMethod.Get, "api/v1/server/info", cancellationToken: ct);
        }
    }
}