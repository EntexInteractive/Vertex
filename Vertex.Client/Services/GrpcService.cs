// Copyright 2026 Entex Interactive

using Microsoft.Extensions.Options;

namespace Vertex.Client.Services
{
    public class GrpcService
    {
        private readonly IOptionsMonitor<ClientSettings> _settings;
    }
}