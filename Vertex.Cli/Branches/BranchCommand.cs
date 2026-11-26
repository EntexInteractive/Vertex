// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Files;
using Vertex.Cli.Net;
using Vertex.Cli.Projects;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Branches
{
    public sealed class BranchCommandSettings : NetworkSettings
    {
        [CommandArgument(0, "[name]")]
        [Description("The branch to create, change or delete, or 'list'. Omit it to show (or with --merge-to, change) the current branch.")]
        public string? Name { get; set; }

        [CommandOption("--delete|-d")]
        [Description("Deletes the branch instead of creating it.")]
        public bool Delete { get; set; }

        [CommandOption("--force|-f")]
        [Description("Deletes without asking for confirmation.")]
        public bool Force { get; set; }

        [CommandOption("--blank")]
        [Description("Creates the branch with no files, instead of a copy of the current branch.")]
        public bool Blank { get; set; }

        [CommandOption("--merge-to")]
        [Description("Merges every revision on the branch into this branch automatically (e.g. release-1.0 into release-2.0). Sets it on a new branch or changes it on an existing one; 'none' stops it.")]
        public string? MergeTo { get; set; }

        [CommandOption("--project|-p")]
        [Description("The project. Defaults to the current workspace's project.")]
        public string? Project { get; set; }
    }

    /// <summary>
    /// Branch commands:
    /// <list type="bullet">
    /// <item><c>vx branch</c> shows the workspace's current branch and where it auto-merges to.</item>
    /// <item><c>vx branch list</c> lists the project's branches on the server.</item>
    /// <item><c>vx branch &lt;name&gt;</c> creates a branch with the current branch's files (or none with
    /// <c>--blank</c>); <c>vx branch -d &lt;name&gt;</c> deletes one.</item>
    /// <item><c>vx branch [name] --merge-to &lt;target&gt;</c> creates the branch with a merge target, or sets or changes
    /// the target of an existing one (the current branch without a name); <c>--merge-to none</c> clears it.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Inside a workspace the project comes from <c>.vx/config</c> with no extra server call, and a new branch is
    /// created from the workspace's current branch (outside one, from <c>main</c>). The server copies the parent's
    /// files as of its latest revision, not the workspace: unsubmitted changes don't go onto the new branch, so
    /// creating one warns about them. <c>list</c> is reserved, so no branch can be named that.
    /// </remarks>
    public sealed class BranchCommand : AsyncCommand<BranchCommandSettings>
    {
        /// <summary>The branch a new branch copies outside a workspace.</summary>
        private const string DefaultParent = "main";

        /// <summary>The <c>--merge-to</c> value that stops auto-merging.</summary>
        private const string NoTarget = "none";

        /// <summary>Unsubmitted changes named in the warning before "and N more".</summary>
        private const int ListedChanges = 5;

        public override async Task<int> ExecuteAsync(CommandContext context, BranchCommandSettings settings, CancellationToken cancellationToken)
        {
            string? name = settings.Name?.Trim();
            VWorkspace? workspace = VWorkspace.Find(Directory.GetCurrentDirectory());
            bool changeTarget = settings.MergeTo is not null;

            if (string.IsNullOrEmpty(name))
            {
                if (settings.Delete)
                {
                    AnsiConsole.MarkupLine("[red]Name the branch to delete, e.g. 'vx branch -d feature-x'.[/]");
                    return 1;
                }

                if (!changeTarget) return await ShowCurrentAsync(settings, workspace, cancellationToken);

                // 'vx branch --merge-to <target>' changes the workspace's own branch.
                name = workspace?.Config.Branch;
                if (name is null)
                {
                    AnsiConsole.MarkupLine("[red]Name the branch to change, e.g. 'vx branch release-1.0 --merge-to release-2.0', or run it inside a workspace.[/]");
                    return 1;
                }
            }

            bool list = name.Equals("list", StringComparison.OrdinalIgnoreCase);
            if (list && (settings.Delete || changeTarget))
            {
                AnsiConsole.MarkupLine("[red]'list' is a command, not a branch name.[/]");
                return 1;
            }

            if (settings.Delete && changeTarget)
            {
                AnsiConsole.MarkupLine("[red]Use --delete or --merge-to, not both.[/]");
                return 1;
            }

            ResolvedProject? project = await ProjectResolver.ResolveAsync(settings, settings.Project, workspace, cancellationToken);
            if (project is null) return 1;
            Guid projectId = project.Id;

            // Only use the workspace's branch when the workspace belongs to the project being changed.
            Guid? currentBranchId = workspace?.Config.ProjectId == projectId ? workspace.Config.BranchId : null;
            string? currentBranch = currentBranchId is null ? null : workspace!.Config.Branch;

            using BranchInterface branches = new(settings.ServerAddress, settings.Token);
            if (list) return await ListAsync(branches, projectId, currentBranchId, cancellationToken);
            if (settings.Delete) return await DeleteAsync(branches, projectId, name, currentBranch, settings.Force, cancellationToken);

            // Look the target up first, so a typo changes nothing.
            VBranch? target = null;
            if (changeTarget && !settings.MergeTo!.Trim().Equals(NoTarget, StringComparison.OrdinalIgnoreCase))
            {
                target = await FindAsync(branches, projectId, project.Name, settings.MergeTo.Trim(), cancellationToken);
                if (target is null) return 1;
            }

            if (changeTarget)
            {
                // An existing branch gets its target changed: release branches are often made before their target exists.
                ServerResult<VBranch> existing = await branches.GetBranchByNameAsync(projectId, name, cancellationToken);
                if (existing.IsSuccessStatusCode) return await SetTargetAsync(branches, projectId, existing.Data!, target, cancellationToken);
                if (existing.StatusCode != 404)
                {
                    AnsiConsole.MarkupLine($"[red]Could not get the branch: {Markup.Escape(existing.Exception?.Message ?? "Unknown error")}[/]");
                    return 1;
                }
            }

            if (settings.Blank) return await CreateAsync(branches, projectId, name, null, null, target, cancellationToken);

            // Outside a workspace of this project, branch from main so a new branch is never empty by accident.
            bool fromWorkspace = currentBranchId is not null;
            if (currentBranchId is null)
            {
                ServerResult<VBranch> main = await branches.GetBranchByNameAsync(projectId, DefaultParent, cancellationToken);
                if (main.IsSuccessStatusCode)
                {
                    currentBranchId = main.Data!.Id;
                    currentBranch = main.Data.Name;
                }
                else if (main.StatusCode == 404)
                {
                    AnsiConsole.MarkupLine($"[red]The project has no '{DefaultParent}' branch to copy. Run this inside a workspace to branch from its branch, or add --blank for an empty branch.[/]");
                    return 1;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Could not get the '{DefaultParent}' branch: {Markup.Escape(main.Exception?.Message ?? "Unknown error")}[/]");
                    return 1;
                }
            }

            // The new branch copies the server, not this folder: find the changes that won't be on it.
            IReadOnlyList<WorkspaceChange> unsubmitted = fromWorkspace ? FindChanges(workspace!, cancellationToken) : [];
            int created = await CreateAsync(branches, projectId, name, currentBranchId, currentBranch, target, cancellationToken);
            if (created == 0 && unsubmitted.Count > 0) WarnUnsubmitted(workspace!, name, currentBranch!, unsubmitted);
            return created;
        }

        private static async Task<int> ShowCurrentAsync(NetworkSettings settings, VWorkspace? workspace, CancellationToken cancellationToken)
        {
            if (workspace?.Config.Branch is not string branch)
            {
                AnsiConsole.MarkupLine(workspace is null
                    ? "[red]Not inside a Vertex workspace. Run 'vx init' or 'vx clone <project>' to create one.[/]"
                    : "[red]This workspace isn't linked to a branch. Run 'vx clone <project>' in an empty folder to make a workspace.[/]");

                return 1;
            }

            string project = workspace.Config.Project is null ? "" : $" in [purple]{Markup.Escape(workspace.Config.Project)}[/]";
            AnsiConsole.MarkupLine($"On branch [purple]{Markup.Escape(branch)}[/]{project}{await DescribeTargetAsync(settings, workspace, cancellationToken)}");
            return 0;
        }

        /// <summary>
        /// ", auto-merging into release-2.0, upstream UE5/release" for the workspace's branch, or nothing when it has
        /// neither or the server can't be asked (showing the branch name never needs the server).
        /// </summary>
        private static async Task<string> DescribeTargetAsync(NetworkSettings settings, VWorkspace workspace, CancellationToken cancellationToken)
        {
            if (workspace.Config.ProjectId is not Guid projectId || workspace.Config.BranchId is not Guid branchId) return "";
            using CancellationTokenSource quick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quick.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using BranchInterface branches = new(settings.ServerAddress, settings.Token);
                ServerResult<IReadOnlyList<VBranch>> all = await branches.ListBranchesAsync(projectId, quick.Token);
                if (!all.IsSuccessStatusCode) return "";
                Guid? target = all.Data!.FirstOrDefault(b => b.Id == branchId)?.MergeTo;
                string? targetName = all.Data!.FirstOrDefault(b => b.Id == target)?.Name;
                string described = targetName is null ? "" : $", auto-merging into [purple]{Markup.Escape(targetName)}[/]";

                ServerResult<IReadOnlyList<VUpstream>> upstreams = await branches.GetUpstreamsAsync(projectId, branchId, false, quick.Token);
                if (upstreams.IsSuccessStatusCode && upstreams.Data!.Count > 0)
                {
                    VUpstream upstream = upstreams.Data[0];
                    string at = upstream.TakenRevision is long taken ? $" #{taken}" : "";
                    string behind = upstream.Behind > 0 ? $", {upstream.Behind:N0} behind" : "";
                    described += $", files from [purple]{Markup.Escape(upstream.Name)}{at}[/]{behind}";
                }

                return described;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return "";
            }
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
                    ? $" [grey](from {Markup.Escape(parentName)}{(branch.BaseRevision > 0 ? $" at #{branch.BaseRevision}" : "")})[/]"
                    : "";
                string target = branch.MergeTo is Guid targetId && names.TryGetValue(targetId, out string? targetName) ? $" -> {Markup.Escape(targetName)}" : "";

                AnsiConsole.MarkupLine(current
                    ? $"[green]* {Markup.Escape(branch.Name)}[/]{parent}{target}"
                    : $"  {Markup.Escape(branch.Name)}{parent}{target}");
            }

            if (all.Count == 0) AnsiConsole.MarkupLine("[grey]No branches.[/]");
            return 0;
        }

        private static async Task<int> CreateAsync(BranchInterface branches, Guid projectId, string name, Guid? parentId, string? parentName, VBranch? target,
            CancellationToken cancellationToken)
        {
            ServerResult<VBranch> result = await branches.CreateBranchAsync(projectId, name, parentId, cancellationToken, target?.Id);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine(result.StatusCode == 409
                    ? $"[red]Branch '{Markup.Escape(name)}' already exists. To change where it auto-merges, run 'vx branch {Markup.Escape(name)} --merge-to <branch>'.[/]"
                    : $"[red]Could not create the branch: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");

                return 1;
            }

            VBranch created = result.Data!;
            string from = parentName is null
                ? " with no files"
                : $" from [purple]{Markup.Escape(parentName)}[/]{(created.BaseRevision > 0 ? $" at [purple]#{created.BaseRevision}[/]" : "")}";
            string merges = target is null ? "" : $", auto-merging into [purple]{Markup.Escape(target.Name)}[/]";
            AnsiConsole.MarkupLine($"Created branch [purple]{Markup.Escape(created.Name)}[/]{from}{merges}. Run 'vx checkout {Markup.Escape(created.Name)}' to switch to it.");
            return 0;
        }

        private static async Task<int> SetTargetAsync(BranchInterface branches, Guid projectId, VBranch branch, VBranch? target, CancellationToken cancellationToken)
        {
            string name = Markup.Escape(branch.Name);
            if (branch.MergeTo == target?.Id)
            {
                AnsiConsole.MarkupLine(target is null
                    ? $"[grey]{name} doesn't auto-merge.[/]"
                    : $"[grey]{name} already auto-merges into {Markup.Escape(target.Name)}.[/]");
                return 0;
            }

            ServerResult<MergeToChange> result = await branches.SetMergeToAsync(projectId, branch.Id, target?.Id, cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine(result.StatusCode == 404
                    ? $"[red]Branch '{name}' no longer exists. Run 'vx branch list' to see the project's branches.[/]"
                    : $"[red]Could not change where {name} auto-merges: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                return 1;
            }

            string was = "none";
            if (result.Data!.Previous is Guid previous)
            {
                ServerResult<VBranch> old = await branches.GetBranchByIdAsync(projectId, previous, cancellationToken);
                was = old.Data?.Name ?? "a deleted branch";
            }

            if (target is null)
            {
                AnsiConsole.MarkupLine($"{name} no longer auto-merges (was: {Markup.Escape(was)}).");
                return 0;
            }

            string into = Markup.Escape(target.Name);
            AnsiConsole.MarkupLine($"[purple]{name}[/] now auto-merges into [purple]{into}[/] (was: {Markup.Escape(was)}).");
            AnsiConsole.MarkupLine($"[grey]From its next revision on. To merge what's on it already, run 'vx merge {name} --into {into}'.[/]");
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
                    409 => $"[red]Can't delete '{Markup.Escape(name)}': {Markup.Escape(result.Exception?.Message ?? "other branches depend on it")}[/]",
                    _ => $"[red]Could not delete the branch: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]"
                });

                return 1;
            }

            AnsiConsole.MarkupLine($"Deleted branch [purple]{Markup.Escape(name)}[/].");
            return 0;
        }

        /// <summary>
        /// Looks up a branch by name, printing the fix when the project has none by that name.
        /// </summary>
        private static async Task<VBranch?> FindAsync(BranchInterface branches, Guid projectId, string projectName, string name, CancellationToken cancellationToken)
        {
            ServerResult<VBranch> result = await branches.GetBranchByNameAsync(projectId, name, cancellationToken);
            if (result.IsSuccessStatusCode) return result.Data!;

            AnsiConsole.MarkupLine(result.StatusCode == 404
                ? $"[red]Project '{Markup.Escape(projectName)}' has no branch '{Markup.Escape(name)}' to merge into. Run 'vx branch list' to see them, or create it first.[/]"
                : $"[red]Could not get the branch: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
            return null;
        }

        /// <summary>
        /// The workspace's unsubmitted changes, from the same scan <c>vx status</c> runs (the stat cache keeps it cheap).
        /// </summary>
        private static IReadOnlyList<WorkspaceChange> FindChanges(VWorkspace workspace, CancellationToken cancellationToken)
        {
            using WorkspaceIndex index = workspace.OpenIndex();
            WorkspaceStatus status = null!;
            AnsiConsole.Status().Start("Checking for unsubmitted changes...", _ =>
            {
                status = WorkspaceScanner.Scan(workspace, index, new ScanOptions { MaxConcurrency = Environment.ProcessorCount }, cancellationToken);
            });

            return status.Changes;
        }

        private static void WarnUnsubmitted(VWorkspace workspace, string branch, string parent, IReadOnlyList<WorkspaceChange> changes)
        {
            string name = Markup.Escape(branch);
            string from = Markup.Escape(parent);
            string count = changes.Count == 1 ? "Your 1 unsubmitted change isn't" : $"Your {changes.Count:N0} unsubmitted changes aren't";
            AnsiConsole.MarkupLine($"[yellow]{count} on {name}: a new branch copies {from} as last submitted, not this workspace.[/]");
            foreach (WorkspaceChange change in changes.Take(ListedChanges))
                AnsiConsole.MarkupLine($"[yellow]  {Markup.Escape(SyncCommand.CommandPath(workspace, change.Path))}[/]");
            if (changes.Count > ListedChanges) AnsiConsole.MarkupLine($"[yellow]  ...and {changes.Count - ListedChanges:N0} more[/]");
            AnsiConsole.MarkupLine($"To put them on {name}: submit them to {from} ('vx submit -m \"...\"'), then 'vx merge {from} --into {name}'.");
        }
    }
}
