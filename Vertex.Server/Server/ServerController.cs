// Copyright 2026 Entex Interactive

using System.Reflection;
using System.Security.Claims;
using EntexInteractive.Extensions.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Net.Responses;
using Vertex.Server.Diagnostics;

namespace Vertex.Server.Server
{
    [Route("api/v1/server")]
    public class ServerController : Controller
    {
        private readonly DiagnosticRepository _diagnostics;
        private readonly ProcessMonitor _monitor;

        public ServerController(DiagnosticRepository diagnostics, ProcessMonitor monitor)
        {
            _diagnostics = diagnostics;
            _monitor = monitor;
        }
        
        /// <summary>
        /// Gets ports used by the server
        /// </summary>
        [HttpGet, AllowAnonymous]
        [Route("info")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<InfoResponse>> GetVersionAsync()
        {
            _monitor.Refresh();
            Assembly assembly = Assembly.GetExecutingAssembly();
            AssemblyName assemblyName = assembly.GetName();
            
            return Ok(new InfoResponse
            {
                Address = HttpContext.Connection.LocalIpAddress?.ToString(),
                BuildDate = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "BuildDate")?.Value,
                Date = DateTime.UtcNow,
                ProcessMs = await _diagnostics.GetProcessTimeAsync(),
                StartTime = _monitor.StartTime,
                Uptime = _monitor.Uptime,
                Version = assemblyName.Version?.ToString()
            });
        }
    }
}
