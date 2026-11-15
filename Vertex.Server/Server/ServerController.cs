// Copyright 2026 Entex Interactive

using System.Reflection;
using System.Security.Claims;
using EntexInteractive.Extensions.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Net.Responses;

namespace Vertex.Server.Server
{
    [Route("api/v1/server")]
    public class ServerController : Controller
    {
        private readonly ProcessMonitor _monitor;

        public ServerController(ProcessMonitor monitor)
        {
            _monitor = monitor;
        }
        
        /// <summary>
        /// Gets ports used by the server
        /// </summary>
        [HttpGet, AllowAnonymous]
        [Route("info")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<InfoResponse> GetServerInfo()
        {
            _monitor.Refresh();
            Assembly assembly = Assembly.GetExecutingAssembly();
            AssemblyName assemblyName = assembly.GetName();
            
            
            
            return Ok(new InfoResponse
            {
                Address = HttpContext.Connection.LocalIpAddress?.ToString(),
                BuildTimeUtc = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "BuildTimeUtc")?.Value,
                Date = DateTime.UtcNow,
                StartTime = _monitor.StartTime,
                Uptime = _monitor.Uptime,
                Version = assemblyName.Version?.ToString()
            });
        }
    }
}
