// Copyright 2026 Entex Interactive

using Entexinteractive.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vertex.Client.Services;

namespace Vertex.Client
{
    internal static class ClientApp
    {
        private static async Task Main(string[] args)
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            CommandLineArguments arguments = new CommandLineArguments(args);
            if (arguments.TryGetValue("-Server", out string? server))
            {
                Console.WriteLine(server);
            }

            builder.Logging.ClearProviders();
            builder.Services.AddSerilog((services, config) => config.ReadFrom.Configuration(builder.Configuration));
            
            builder.Services.AddMemoryCache();
            builder.Services.AddSingleton<GrpcService>();
            
            IHost host = builder.Build();
            await host.RunAsync();
        }
    }
}