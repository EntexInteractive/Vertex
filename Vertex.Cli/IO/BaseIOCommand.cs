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
        /// Indicates whether directories should be processed recursively.
        /// </summary>
        protected readonly Option<bool> RecursiveOption = new("--recursive", ["-R"]) { Description = "Recursively process directories." };

        /// <summary>
        /// Forces the operation to proceed by bypassing applicable safety checks and confirmation safeguards.
        /// </summary>
        protected readonly Option<bool> ForceOption = new("--force", ["-f"]) { Description = "Force the operation bypassing safeguards." };

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
            this.Add(RecursiveOption);
            this.Add(ForceOption);
        }
    }
}