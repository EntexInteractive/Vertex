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
            
            AssemblyName assemblyName = Assembly.GetExecutingAssembly().GetName();
            return Ok(new InfoResponse
            {
                Address = HttpContext.Connection.LocalIpAddress?.ToString(),
                CpuUsage = Convert.ToInt32(_monitor.CpuUsage),
                Date = DateTime.UtcNow,
                ProcessMs = await _diagnostics.GetProcessTimeAsync(),
                RamUsage = _monitor.RamUsage,
                Uptime = _monitor.Uptime,
                Version = assemblyName.Version?.ToString()
            });
        }
    }
}
