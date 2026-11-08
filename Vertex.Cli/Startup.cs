// Copyright 2026 Entex Interactive

using Microsoft.Extensions.DependencyInjection;
using Vertex.Core.Settings;

namespace Vertex.Cli
{
    public class Startup
    {
        public static ServiceProvider CreateServiceProvider()
        {
            ServiceCollection services = new();
            return services.BuildServiceProvider();
        }
    }
}