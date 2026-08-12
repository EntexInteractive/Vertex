// Copyright 2026 Entex Interactive

using Microsoft.Extensions.Options;

namespace Vertex.Agent.Services
{
    public class GrpcService
    {
        private readonly IOptionsMonitor<ClientSettings> _settings;
    }
}