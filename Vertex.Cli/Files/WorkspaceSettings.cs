// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;
using Vertex.Core.Client.Workspaces;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Settings for commands that work on the workspace containing the current folder (submit, sync, revert). The
    /// project and branch always come from the workspace.
    /// </summary>
    public class WorkspaceSettings : NetworkSettings
    {
        [CommandOption("--parallel")]
        [Description("The number of parallel operations to perform.")]
        public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// The server, login and parallelism for workspace operations.
        /// </summary>
        public WorkspaceConnection Connection => new(ServerAddress, Token, Credential?.Username, MaxConcurrency);
    }
}
