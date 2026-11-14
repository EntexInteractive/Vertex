// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using EntexInteractive.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Vertex.Server.Auth
{
    /// <summary>
    /// Authenticates <c>Authorization: Bearer vxp_...</c> on REST and gRPC calls alike.
    /// </summary>
    /// <remarks>
    /// The session comes from the cache, so an authenticated call normally costs no database round trip. Token use is
    /// recorded at most once a minute per token per server instance, so the hot path does no writes either.
    /// </remarks>
    public sealed class TokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private static readonly ConcurrentDictionary<Guid, long> LastTouched = new();
        private static readonly long TouchIntervalMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

        private readonly TokensRepository _tokens;

        public TokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TokensRepository tokens)
            : base(options, logger, encoder)
        {
            _tokens = tokens;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? header = Request.Headers.Authorization;
            if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

            string token = header["Bearer ".Length..].Trim();
            if (!token.StartsWith(AccessTokens.Prefix, StringComparison.Ordinal)) return AuthenticateResult.Fail("Unrecognized token.");

            TokenSession? session = await _tokens.FindSessionAsync(AccessTokens.Hash(token));
            long now = (long)UnixTime.Now.TotalMilliseconds;
            if (session is null || session.ExpiresAt <= now) return AuthenticateResult.Fail("The token is invalid or expired.");

            long last = LastTouched.GetValueOrDefault(session.TokenId);
            if (now - last >= TouchIntervalMs)
            {
                LastTouched[session.TokenId] = now;
                await _tokens.TouchAsync(session.TokenId, Context.RequestAborted);
            }

            List<Claim> claims =
            [
                new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
                new(ClaimTypes.Name, session.Username),
                new(VertexClaims.TokenId, session.TokenId.ToString())
            ];

            if (session.IsAdmin) claims.Add(new Claim(VertexClaims.Admin, "true"));

            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, VertexClaims.Scheme));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, VertexClaims.Scheme));
        }
    }
}
