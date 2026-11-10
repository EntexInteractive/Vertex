// Copyright 2026 Entex Interactive

using Vertex.Cli.Net;

namespace Vertex.Cli.Projects
{
    public abstract class BaseProjectCommand : BaseNetCommand
    {
        /// <summary>
        /// Specifies the branch to use for the command.
        /// </summary>
        protected readonly Option<string> ProjectOption = new("--project", ["-p"]) { Description = "Name of the project to use." };

        /// <summary>
        /// Provides a common base command for project-related CLI commands.
        /// </summary>
        protected BaseProjectCommand(string name, string description) : base(name, description)
        {
            this.Add(ProjectOption);
        }
    }
}