// Copyright 2026 Entex Interactive

using Entexinteractive.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Vertex.Agent.Services;

namespace Vertex.Agent
{
    internal static class AgentApp
    {
        private static async Task Main(string[] args)
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            CommandLineArguments arguments = new CommandLineArguments(args);
            if (arguments.TryGetValue("-Server", out string? serverAddress))
            {
                
            }

            builder.Logging.ClearProviders();
            builder.Services.AddSerilog((services, config) =>
            {
                config.ReadFrom.Configuration(builder.Configuration);
            });
            
            builder.Services.AddMemoryCache();
            builder.Services.Configure<AgentSettings>(builder.Configuration.GetSection(AgentSettings.SectionName));
            builder.Services.AddSingleton<GrpcService>();
            
            IHost host = builder.Build();
            GrpcService grpc = host.Services.GetRequiredService<GrpcService>();
            Console.WriteLine(await grpc.SayHelloAsync(Environment.MachineName));

            await host.RunAsync();
        }
    }
}
