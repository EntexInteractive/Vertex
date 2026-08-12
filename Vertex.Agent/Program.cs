// Copyright 2026 Entex Interactive

using Microsoft.Extensions.Hosting;

namespace Vertex.Agent
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        }
    }
}