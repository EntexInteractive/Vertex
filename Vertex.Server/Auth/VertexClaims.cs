// Copyright 2026 Entex Interactive

using System.Security.Claims;

namespace Vertex.Server.Auth
{
    /// <summary>
    /// Claims put on the authenticated user, and helpers to read them in controllers and services.
    /// </summary>
    public static class VertexClaims
    {
        public const string Scheme = "VertexToken";
        public const string Admin = "vx:admin";
        public const string TokenId = "vx:token";

        public static Guid GetUserId(this ClaimsPrincipal user)
        {
            return Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("The request is not authenticated."));
        }

        public static string GetUsername(this ClaimsPrincipal user)
        {
            return user.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        }

        public static Guid GetTokenId(this ClaimsPrincipal user)
        {
            return Guid.Parse(user.FindFirstValue(TokenId) ?? throw new InvalidOperationException("The request is not authenticated."));
        }

        public static bool IsServerAdmin(this ClaimsPrincipal user)
        {
            return user.HasClaim(Admin, "true");
        }
    }
}
