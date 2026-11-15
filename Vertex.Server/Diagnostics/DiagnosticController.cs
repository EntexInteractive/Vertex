// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Vertex.Server.Diagnostics
{
    [Route("api/v1/diagnostics")]
    public class DiagnosticController : Controller
    {
        private readonly DiagnosticRepository _diagnostics;
        private readonly ProcessMonitor _monitor;

        public DiagnosticController(DiagnosticRepository diagnostics, ProcessMonitor monitor)
        {
            _diagnostics = diagnostics;
            _monitor = monitor;
        }
    }
}