// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Workspaces
{
    public sealed class FetchSettings : NetworkSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("The name of the project on the server.")]
        public string Project { get; set; } = string.Empty;

        [CommandArgument(1, "[path]")]
        [Description("Folder to link. Defaults to the current folder.")]
        public string? Path { get; set; }

        [CommandOption("--branch|-b")]
        [Description("The branch to follow. Defaults to 'main'.")]
        public string Branch { get; set; } = "main";
    }

    /// <summary>
    /// Links a folder to a project that already exists on the server.
    /// </summary>
    public sealed class FetchCommand : AsyncCommand<FetchSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, FetchSettings settings, CancellationToken cancellationToken)
        {
            using ProjectInterface projects = new(settings.ServerAddress);
            ServerResult<VProject> projectResult = await projects.GetProjectByNameAsync(settings.Project, cancellationToken);
            if (!projectResult.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine(projectResult.StatusCode == 404
                    ? $"[red]Project '{Markup.Escape(settings.Project)}' does not exist. Use 'vx init' to create it.[/]"
                    : $"[red]Could not get the project: {Markup.Escape(projectResult.Exception?.Message ?? "Unknown error")}[/]");

                return 1;
            }

            VProject project = projectResult.Data!;
            using BranchInterface branches = new(settings.ServerAddress);
            ServerResult<VBranch> branchResult = await branches.GetBranchByNameAsync(project.Id, settings.Branch, cancellationToken);
            if (!branchResult.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine(branchResult.StatusCode == 404
                    ? $"[red]Project '{Markup.Escape(project.Name)}' has no branch '{Markup.Escape(settings.Branch)}'.[/]"
                    : $"[red]Could not get the branch: {Markup.Escape(branchResult.Exception?.Message ?? "Unknown error")}[/]");

                return 1;
            }

            VBranch branch = branchResult.Data!;
            string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory()));

            // Fetching again into a folder already linked to this project and branch is a no-op, not an error.
            VWorkspace? existing = VWorkspace.Find(root);
            if (existing is not null)
            {
                bool sameRoot = string.Equals(existing.Root, root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (sameRoot && existing.Config.ProjectId == project.Id && existing.Config.BranchId == branch.Id)
                {
                    AnsiConsole.MarkupLine($"[purple]{Markup.Escape(root)}[/] is already linked to [purple]{Markup.Escape(project.Name)}/{Markup.Escape(branch.Name)}[/].");
                    return 0;
                }

                string linkedTo = existing.Config.Project is null ? "" : $" (linked to {existing.Config.Root})";
                AnsiConsole.MarkupLine($"[red]'{Markup.Escape(existing.Root)}' is already a Vertex workspace{Markup.Escape(linkedTo)}.[/]");
                return 1;
            }

            VWorkspace workspace = VWorkspace.Initialize(root, new WorkspaceConfig
            {
                Address = settings.ServerAddress,
                Project = project.Name,
                ProjectId = project.Id,
                Branch = branch.Name,
                BranchId = branch.Id
            });

            AnsiConsole.MarkupLine($"Linked [purple]{Markup.Escape(workspace.Root)}[/] to [purple]{Markup.Escape(project.Name)}/{Markup.Escape(branch.Name)}[/].");
            return 0;
        }
    }
}