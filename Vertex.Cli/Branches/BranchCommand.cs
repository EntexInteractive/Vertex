// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Branches
{
    public sealed class BranchCommandSettings : NetworkSettings
    {
        [CommandArgument(0, "[name]")]
        [Description("The branch to create or delete, or 'list'. Omit it to show the current branch.")]
        public string? Name { get; set; }

        [CommandOption("--delete|-d")]
        [Description("Deletes the branch instead of creating it.")]
        public bool Delete { get; set; }

        [CommandOption("--force|-f")]
        [Description("Deletes without asking for confirmation.")]
        public bool Force { get; set; }

        [CommandOption("--project|-p")]
        [Description("The project. Defaults to the current workspace's project.")]
        public string? Project { get; set; }
    }

    /// <summary>
    /// Branch commands:
    /// <list type="bullet">
    /// <item><c>vx branch</c> shows the workspace's current branch (local, no server call).</item>
    /// <item><c>vx branch list</c> lists the project's branches on the server.</item>
    /// <item><c>vx branch &lt;name&gt;</c> creates a branch; <c>vx branch -d &lt;name&gt;</c> deletes one.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Inside a workspace the project comes from <c>.vx/config</c> with no extra server call, and a new branch is
    /// created from the workspace's current branch. <c>list</c> is reserved, so no branch can be named that.
    /// </remarks>
    public sealed class BranchCommand : AsyncCommand<BranchCommandSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, BranchCommandSettings settings, CancellationToken cancellationToken)
        {
            string? name = settings.Name?.Trim();
            VWorkspace? workspace = VWorkspace.Find(Directory.GetCurrentDirectory());

            if (string.IsNullOrEmpty(name))
            {
                if (settings.Delete)
                {
                    AnsiConsole.MarkupLine("[red]Name the branch to delete, e.g. 'vx branch -d feature-x'.[/]");
                    return 1;
                }

                return ShowCurrent(workspace);
            }

            bool list = name.Equals("list", StringComparison.OrdinalIgnoreCase);
            if (list && settings.Delete)
            {
                AnsiConsole.MarkupLine($"[red]'list' is a command, not a branch name.[/]");
                return 1;
            }

            Guid? projectId = await ResolveProjectAsync(settings, workspace, cancellationToken);
            if (projectId is null) return 1;

            // Only use the workspace's branch when the workspace belongs to the project being changed.
            Guid? currentBranchId = workspace?.Config.ProjectId == projectId ? workspace.Config.BranchId : null;
            string? currentBranch = currentBranchId is null ? null : workspace!.Config.Branch;

            using BranchInterface branches = new(settings.ServerAddress);
            if (list) return await ListAsync(branches, projectId.Value, currentBranchId, cancellationToken);
            return settings.Delete ? await DeleteAsync(branches, projectId.Value, name, currentBranch, settings.Force, cancellationToken) : await CreateAsync(branches, projectId.Value, name, currentBranchId, currentBranch, cancellationToken);
        }

        private static int ShowCurrent(VWorkspace? workspace)
        {
            if (workspace?.Config.Branch is not string branch)
            {
                AnsiConsole.MarkupLine(workspace is null
                    ? "[red]Not inside a Vertex workspace. Run 'vx init' or 'vx fetch <project>' to create one.[/]"
                    : "[red]This workspace isn't linked to a branch. Run 'vx fetch <project>' to link it.[/]");

                return 1;
            }

            string project = workspace.Config.Project is null ? "" : $" in [purple]{Markup.Escape(workspace.Config.Project)}[/]";
            AnsiConsole.MarkupLine($"On branch [purple]{Markup.Escape(branch)}[/]{project}");
            return 0;
        }

        private static async Task<int> ListAsync(BranchInterface branches, Guid projectId, Guid? currentBranchId, CancellationToken cancellationToken)
        {
            ServerResult<IReadOnlyList<VBranch>> result = await branches.ListBranchesAsync(projectId, cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Could not list branches: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                return 1;
            }

            IReadOnlyList<VBranch> all = result.Data!;
            Dictionary<Guid, string> names = all.ToDictionary(b => b.Id, b => b.Name);
            foreach (VBranch branch in all)
            {
                bool current = branch.Id == currentBranchId;
                string parent = branch.ParentId is Guid parentId && names.TryGetValue(parentId, out string? parentName)
                    ? $" [grey](from {Markup.Escape(parentName)})[/]"
                    : "";

                AnsiConsole.MarkupLine(current
                    ? $"[green]* {Markup.Escape(branch.Name)}[/]{parent}"
                    : $"  {Markup.Escape(branch.Name)}{parent}");
            }

            if (all.Count == 0) AnsiConsole.MarkupLine("[grey]No branches.[/]");
            return 0;
        }

        private static async Task<int> CreateAsync(BranchInterface branches, Guid projectId, string name, Guid? parentId, string? parentName, CancellationToken cancellationToken)
        {
            ServerResult<VBranch> result = await branches.CreateBranchAsync(projectId, name, parentId, cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine(result.StatusCode == 409
                    ? $"[red]Branch '{Markup.Escape(name)}' already exists.[/]"
                    : $"[red]Could not create the branch: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");

                return 1;
            }

            string from = parentName is null ? "" : $" from [purple]{Markup.Escape(parentName)}[/]";
            AnsiConsole.MarkupLine($"Created branch [purple]{Markup.Escape(result.Data!.Name)}[/]{from}.");
            return 0;
        }

        private static async Task<int> DeleteAsync(BranchInterface branches, Guid projectId, string name, string? currentBranch, bool force, CancellationToken cancellationToken)
        {
            if (string.Equals(name, currentBranch, StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine($"[red]Can't delete '{Markup.Escape(name)}': this workspace is on it.[/]");
                return 1;
            }

            // Branches live on the server, so deleting one removes it for everyone. Ask unless told not to.
            if (!force)
            {
                if (Console.IsInputRedirected)
                {
                    AnsiConsole.MarkupLine("[red]Deleting a branch needs confirmation. Use --force to skip it.[/]");
                    return 1;
                }

                if (!await AnsiConsole.ConfirmAsync($"Delete branch [purple]{Markup.Escape(name)}[/] from the server for everyone?", false, cancellationToken))
                    return 1;
            }

            ServerResult<VBranch> result = await branches.DeleteBranchAsync(projectId, name, cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine(result.StatusCode switch
                {
                    404 => $"[red]Branch '{Markup.Escape(name)}' does not exist.[/]",
                    409 => $"[red]Can't delete '{Markup.Escape(name)}': other branches were created from it. Delete those first.[/]",
                    _ => $"[red]Could not delete the branch: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]"
                });

                return 1;
            }

            AnsiConsole.MarkupLine($"Deleted branch [purple]{Markup.Escape(name)}[/].");
            return 0;
        }

        /// <summary>
        /// Uses <c>--project</c> when given (one server lookup), otherwise the workspace's project id (no lookup).
        /// </summary>
        private static async Task<Guid?> ResolveProjectAsync(BranchCommandSettings settings, VWorkspace? workspace, CancellationToken cancellationToken)
        {
            if (settings.Project is null)
            {
                if (workspace?.Config.ProjectId is Guid id) return id;
                AnsiConsole.MarkupLine("[red]Not inside a Vertex workspace. Run this from a workspace or pass --project.[/]");
                return null;
            }

            using ProjectInterface projects = new(settings.ServerAddress);
            ServerResult<VProject> result = await projects.GetProjectByNameAsync(settings.Project, cancellationToken);
            if (result.IsSuccessStatusCode) return result.Data!.Id;

            AnsiConsole.MarkupLine(result.StatusCode == 404
                ? $"[red]Project '{Markup.Escape(settings.Project)}' does not exist.[/]"
                : $"[red]Could not get the project: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");

            return null;
        }
    }
}
