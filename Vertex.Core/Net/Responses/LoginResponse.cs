// Copyright 2026 Entex Interactive

using Vertex.Core.Data;

namespace Vertex.Core.Net.Responses
{
    /// <summary>
    /// Returned by POST /api/v1/auth/login: the new token (shown only once) and the user it belongs to.
    /// </summary>
    public sealed record LoginResponse(string Token, VUser User);
}
