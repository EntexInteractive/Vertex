// Copyright 2026 Entex Interactive

using Vertex.Cli.Branches;
using Vertex.Cli.Net;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Represents a base command for I/O-related CLI operations that build on shared network command behavior.
    /// </summary>
    public abstract class BaseFilesCommand : BaseBranchCommand
    {
        /// <summary>
        /// Specifies the file or directory path to use for the command.
        /// </summary>
        protected readonly Option<string> PathOption = new("--path") { Description = "Path to a file or directory." };
        
        /// <summary>
        /// Forces the operation to proceed by bypassing applicable safety checks and confirmation safeguards.
        /// </summary>
        protected readonly Option<bool> ForceOption = new("--force", ["-f"]) { Description = "Forces the operation bypassing safeguards." };

        /// <summary>
        /// Specifies the number of operations to execute in parallel for the command.
        /// </summary>
        protected readonly Option<int> ParallelOption = new("--parallel") { Description = "The number of parallel executions." };

        /// <summary>
        /// Provides a base command for I/O-related CLI operations that require common network
        /// and filesystem-oriented command options.
        /// </summary>
        /// <param name="name">The command name.</param>
        /// <param name="description">The command description.</param>
        protected BaseFilesCommand(string name, string description) : base(name, description)
        {
            this.Add(PathOption);
            this.Add(ForceOption);
            this.Add(ParallelOption);
        }
    }
}