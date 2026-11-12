// Copyright 2026 Entex Interactive

using Vertex.Cli.Branches;
using Vertex.Core.Client.Workspaces;

namespace Vertex.Cli.Workspaces
{
    /// <summary>
    /// Turns a folder into a Vertex workspace by creating <c>.vx/</c> in it.
    /// </summary>
    public sealed class InitCommand : Command<BranchSettings>
    {
        public override int Execute(CommandContext context, BranchSettings settings, CancellationToken cancellationToken)
        {
            Workspace workspace = Workspace.Initialize(settings.RootPath, new WorkspaceConfig
            {
                Address = settings.ServerAddress,
                Project = settings.Project,
                Branch = settings.Branch
            });

            AnsiConsole.MarkupLine($"Initialized Vertex workspace in [purple]{Markup.Escape(workspace.MetadataDirectory)}[/]");
            return 0;
        }
    }
}