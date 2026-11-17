// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
        /// <summary>
        /// Gets ports used by the server
        /// </summary>
        [HttpGet, AllowAnonymous]
        [Route("info")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<InfoResponse> GetServerInfo()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            AssemblyName assemblyName = assembly.GetName();
            
            using Process process = Process.GetCurrentProcess();
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            
            return Ok(new InfoResponse
            {
                Address = HttpContext.Connection.LocalIpAddress?.ToString(),
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                BuildTimeUtc = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "BuildTimeUtc")?.Value,
                Date = DateTime.UtcNow,
                Encrypted = HttpContext.Request.IsHttps,
                Framework = RuntimeInformation.FrameworkDescription,
                MachineName = Environment.MachineName,
                OperatingSystem = RuntimeInformation.OSDescription,
                ProcessUptime = DateTime.Now - process.StartTime,
                ProcessorCount = Environment.ProcessorCount,
                SystemUptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
                Version = assemblyName.Version?.ToString()
            });
        }
    }
}
