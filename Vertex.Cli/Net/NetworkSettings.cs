// Copyright 2026 Entex Interactive

using Spectre.Console.Cli;

namespace Vertex.Cli.Net
{
    public class NetworkSettings : CommandSettings
    {
        /// <summary>
        /// The server listens on clear-text HTTP/2 (h2c), so the default must be http, not https.
        /// </summary>
        public const string DefaultServer = "http://localhost:5000";

        [CommandOption("--server|-s")] public string? Server { get; set; }

        public string ServerAddress => Server ?? DefaultServer;

        [CommandOption("--verbose|-v")] public bool Verbose { get; set; }
    }
}