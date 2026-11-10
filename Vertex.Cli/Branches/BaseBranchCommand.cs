// Copyright 2026 Entex Interactive

using Vertex.Cli.Projects;

namespace Vertex.Cli.Branches
{
    public abstract class BaseBranchCommand : BaseProjectCommand
    {
        /// <summary>
        /// Specifies the branch to use for the command.
        /// </summary>
        protected readonly Option<string> BranchOption = new("--branch", ["-b"]) { Description = "Name of the branch to use." };

        protected BaseBranchCommand(string name, string description) : base(name, description)
        {
            this.Add(BranchOption);
        }
    }
}