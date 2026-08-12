// Copyright 2026 Entex Interactive

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vertex.Agent.Services;

namespace Vertex.Agent
{
    internal static class ClientApp
    {
        private static async Task Main(string[] args)
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddSingleton<GrpcService>();
            
            IHost host = builder.Build();
            await host.RunAsync();
        }
    }
}