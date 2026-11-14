// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using EntexInteractive.Extensions.Diagnostics;
using Microsoft.AspNetCore.Http.Extensions;
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
            _logger.LogDebug("Received request: '{ToLower}' from '{ConnectionRemoteIpAddress}'", context.Request.Path.ToString().ToLower(), context.Connection.RemoteIpAddress);
            Stopwatch sw = Stopwatch.StartNew();
            
            await next(context);
            _monitor.Refresh();

            string endpoint = context.Request.GetDisplayUrl();
            if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return;
            
            string? uuid = context.User.Claims.FirstOrDefault(u => u.Type == ClaimTypes.NameIdentifier)?.Value;
            string method = context.Request.Method.ToUpper();
            int statusCode = context.Response.StatusCode;
            long processMs = sw.ElapsedMilliseconds;
            long length = Convert.ToInt64(context.Request.ContentLength ?? 0 + context.Response.ContentLength ?? 0);

            _logger.LogInformation("Processed request: '{Endpoint}' in {SwElapsedMilliseconds}ms (Authenticated: {IdentityIsAuthenticated})", endpoint, processMs, context.User.Identity?.IsAuthenticated);
            VRequest request = new(Guid.TryParse(uuid, out Guid userId) ? userId : null, method, endpoint, statusCode, processMs, length);
            await _db.AddRequestAsync(request);
        }
    }
}