// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Spectre.Console.Cli;

namespace Vertex.Cli.Net
{
    public class NetworkSettings : CommandSettings
    {
        [CommandOption("--server|-s")]
        [Description("The server address to use.")]
        public string? ServerAddress { get; set; } = "http://127.0.0.1:5000";

        [CommandOption("--verbose|-v")]
        [Description("Displays verbose output.")]
        public bool Verbose { get; set; }
    }
}