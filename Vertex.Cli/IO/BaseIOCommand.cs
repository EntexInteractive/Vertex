// Copyright 2026 Entex Interactive

using Vertex.Cli.Net;
using Vertex.Core.Contexts;

namespace Vertex.Cli.IO
{
    public abstract class BaseIOCommand : BaseNetCommand
    {
        /// <summary>
        /// Specifies the branch to use for the command.
        /// </summary>
        protected readonly Option<string> BranchOption = new("--branch", ["-b"]) { Description = "Path to a file or directory." };

        /// <summary>
        /// Specifies the file or directory path to use for the command.
        /// </summary>
        protected readonly Option<string> PathOption = new("--path", ["-p"]) { Description = "Path to a file or directory." };

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
        protected BaseIOCommand(string name, string description) : base(name, description)
        {
            this.Add(BranchOption);
            this.Add(PathOption);
            this.Add(ForceOption);
            this.Add(ParallelOption);
        }
    }
}