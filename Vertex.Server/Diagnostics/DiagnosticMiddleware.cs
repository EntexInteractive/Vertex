// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using EntexInteractive.Extensions.Diagnostics;
using Vertex.Core.Data;

namespace Vertex.Server.Diagnostics
{
    /// <summary>
    /// Represents the middleware for request diagnostics.
    /// </summary>
    public class DiagnosticMiddleware : IMiddleware
    {
        private readonly ILogger<DiagnosticMiddleware> _logger;
        private readonly DiagnosticRepository _db;
        private readonly ProcessMonitor _monitor;

        /// <summary>
        /// Initializes a new <see cref="DiagnosticMiddleware"/> instance.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="db"></param>
        /// <param name="monitor"></param>
        public DiagnosticMiddleware(ILogger<DiagnosticMiddleware> logger, DiagnosticRepository db, ProcessMonitor monitor)
        {
            _logger = logger;
            _db = db;
            _monitor = monitor;
        }

        /// <summary>
        /// Logs the request processing time in the <see cref="DiagnosticRepository"/>.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="next"></param>
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            _logger.LogDebug($"Received request: '{context.Request.Path.ToString().ToLower()}' from '{context.Connection.RemoteIpAddress}'");
            Stopwatch sw = Stopwatch.StartNew();
            await next(context);

            string endpoint = context.Request.Path.ToString().ToLower();
            if (!endpoint.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return;

            _monitor.Refresh();
            string? uuid = context.User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
            string method = context.Request.Method.ToUpper();
            int statusCode = context.Response.StatusCode;
            long processMs = sw.ElapsedMilliseconds;
            long length = Convert.ToInt64(context.Request.ContentLength ?? 0 + context.Response.ContentLength ?? 0);

            _logger.LogDebug("Processed request: '{Endpoint}' in {SwElapsedMilliseconds}ms (Authenticated: {IdentityIsAuthenticated})", endpoint, sw.ElapsedMilliseconds, context.User.Identity?.IsAuthenticated);
            VRequest request = new(null, method, endpoint, statusCode, processMs, length);
            await _db.AddRequestAsync(request);
        }
    }
}