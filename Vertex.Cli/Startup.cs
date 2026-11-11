// Copyright 2026 Entex Interactive

using Microsoft.Extensions.DependencyInjection;

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