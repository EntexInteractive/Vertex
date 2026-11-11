// Copyright 2026 Entex Interactive

using Spectre.Console.Cli;

namespace Vertex.Cli.Net
{
    public class NetworkSettings : CommandSettings
    {
        [CommandOption("--server|-s")] public string? Server { get; set; }

        public string ServerAddress => Server ?? "http://localhost:5000";

        [CommandOption("--verbose|-v")] public bool Verbose { get; set; }
    }
}