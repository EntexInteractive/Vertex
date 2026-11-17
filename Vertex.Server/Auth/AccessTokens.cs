// Copyright 2026 Entex Interactive

using System.Security.Cryptography;
using System.Text;

namespace Vertex.Server.Auth
{
    /// <summary>
    /// Opaque login tokens: <c>vxp_</c> followed by 32 random bytes in base64url. The server stores only their
    /// SHA-256, so a database leak doesn't leak usable tokens, and revoking one takes effect immediately.
    /// </summary>
    public static class AccessTokens
    {
        public const string Prefix = "vxt_";

        /// <summary>
        /// A token expires after this long without being used. Each use pushes the expiry forward.
        /// </summary>
        public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(90);

        public static string Create()
        {
            return Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] Hash(string token)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(token));
        }

        /// <summary>
        /// The cache key for a token's session. Keyed by hash so the token itself is never stored in Redis.
        /// </summary>
        public static string CacheKey(byte[] hash)
        {
            return "auth:token:" + Convert.ToHexStringLower(hash);
        }
    }
}
