// Copyright 2026 Entex Interactive

namespace Vertex.Cli.Net
{
    public abstract class BaseNetCommand : Command
    {
        /// <summary>
        /// Specifies the server host URL to use when connecting to the target server.
        /// </summary>
        protected readonly Option<string> ServerOption = new("--server", ["-s"]) { Description = "The Vertex server url." };

        /// <summary>
        /// Enables verbose logging output for more detailed command execution information.
        /// </summary>
        protected readonly Option<bool> VerboseOption = new("--verbose", ["-v"]) { Description = "Display with verbose logging." };

        protected BaseNetCommand(string name, string description) : base(name, description) { }
    }
}