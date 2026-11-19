// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;

namespace Vertex.Gui.Hosting
{
    /// <summary>
    /// Guards the local API. Kestrel listens on loopback only, but any process on the machine, and any web page in the
    /// user's browser, can still send requests to it, so every <c>/api</c> call must:
    /// <list type="number">
    /// <item>name our own loopback address in <c>Host</c>, which defeats DNS rebinding;</item>
    /// <item>come from an allowed <c>Origin</c> when the browser sends one;</item>
    /// <item>carry the per-launch token in <see cref="HostIdentity.TokenHeader"/>. A custom header makes browsers send a
    /// CORS preflight first, and since no CORS policy is registered, other origins can never get that far.</item>
    /// </list>
    /// Static files stay public: they hold no secrets.
    /// </summary>
    public sealed class HostSecurityMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly HostIdentity _identity;
        private readonly ILogger<HostSecurityMiddleware> _logger;

        public HostSecurityMiddleware(RequestDelegate next, HostIdentity identity, ILogger<HostSecurityMiddleware> logger)
        {
            _next = next;
            _identity = identity;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await _next(context);
                return;
            }

            string? reason = Check(context.Request);
            if (reason is null)
            {
                await _next(context);
                return;
            }

            _logger.LogWarning("Rejected {Method} {Path}: {Reason}", context.Request.Method, context.Request.Path, reason);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = "Only the Vertex desktop window can call this API."
            }, options: null, contentType: "application/problem+json");
        }

        private string? Check(HttpRequest request)
        {
            if (!_identity.IsAllowedHost(request.Host.Value ?? string.Empty)) return $"unexpected Host '{request.Host}'";

            string origin = request.Headers.Origin.ToString();
            if (origin.Length > 0 && !_identity.IsAllowedOrigin(origin)) return $"unexpected Origin '{origin}'";

            if (!_identity.IsValidToken(request.Headers[HostIdentity.TokenHeader].ToString())) return "missing or wrong token";

            return null;
        }
    }
}
