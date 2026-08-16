// Copyright 2026 Entex Interactive

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Vertex.Server.Server
{
    [Route("api/v1/server")]
    public class ServerController : Controller
    {
        /// <summary>
        /// Gets ports used by the server
        /// </summary>
        [HttpGet, AllowAnonymous]
        [Route("ports")]
        public ActionResult<GetPortsResponse> GetPorts()
        {
            GetPortsResponse response = new GetPortsResponse();
            response.HttpPort = Convert.ToInt32(Environment.GetEnvironmentVariable("Http_Port") ?? "5000");
            response.GrpcPort = response.HttpPort;
            return response;
        }
        
        /// <summary>
        /// Gets ports used by the server
        /// </summary>
        [HttpGet, AllowAnonymous]
        [Route("version")]
        public ActionResult GetVersion()
        {
            AssemblyName assemblyName = Assembly.GetExecutingAssembly().GetName();
            return Content(assemblyName.Version?.ToString() ?? string.Empty, "text/plain");
        }
        
        /*/// <summary>
        /// Get server information
        /// </summary>
        [HttpGet]
        [AllowAnonymous]
        [Route("/api/v1/server/info")]
        [ProducesResponseType(typeof(GetServerInfoResponse), 200)]
        public async Task<ActionResult<GetServerInfoResponse>> GetServerInfoAsync()
        {
            th
        }*/
    }
}
