// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using Vertex.Cli.Files;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Workspaces
{
    public sealed class CloneSettings : WorkspaceSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("The name of the project on the server.")]
        public string Project { get; set; } = string.Empty;

        [CommandArgument(1, "[path]")]
        [Description("Folder to clone into. Defaults to the current folder.")]
        public string? Path { get; set; }

        [CommandOption("--branch|-b")]
        [Description("The branch to follow. Defaults to 'main'.")]
        public string Branch { get; set; } = "main";
    }

    /// <summary>
    /// Makes a folder a workspace of a project that already exists on the server and downloads the branch's latest
    /// files into it.
    /// </summary>
    /// <remarks>
    /// Files already in the folder are never overwritten: one identical to the server's is adopted as is, and one that
    /// differs is reported as a conflict. Running it again in a folder already cloned from the same branch finishes an
    /// interrupted download, the same as <c>vx sync</c>.
    /// </remarks>
    public sealed class CloneCommand : AsyncCommand<CloneSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, CloneSettings settings, CancellationToken cancellationToken)
        {
            Stopwatch timer = Stopwatch.StartNew();
            using ProjectInterface projects = new(settings.ServerAddress, settings.Token);
            ServerResult<VProject> projectResult = await projects.GetProjectByNameAsync(settings.Project, cancellationToken);
            if (!projectResult.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine(projectResult.StatusCode == 404
                    ? $"[red]Project '{Markup.Escape(settings.Project)}' does not exist. Use 'vx init' to create it.[/]"
                    : $"[red]Could not get the project: {Markup.Escape(projectResult.Exception?.Message ?? "Unknown error")}[/]");

                return 1;
            }

            VProject project = projectResult.Data!;
            using BranchInterface branches = new(settings.ServerAddress, settings.Token);
            ServerResult<VBranch> branchResult = await branches.GetBranchByNameAsync(project.Id, settings.Branch, cancellationToken);
            if (!branchResult.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine(branchResult.StatusCode == 404
                    ? $"[red]Project '{Markup.Escape(project.Name)}' has no branch '{Markup.Escape(settings.Branch)}'. Run 'vx branch list' in a workspace of it to see them.[/]"
                    : $"[red]Could not get the branch: {Markup.Escape(branchResult.Exception?.Message ?? "Unknown error")}[/]");

                return 1;
            }

            VBranch branch = branchResult.Data!;
            string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory()));

            // Cloning again into a folder already cloned from this branch just finishes the download.
            VWorkspace? workspace = VWorkspace.Find(root);
            if (workspace is not null)
            {
                bool sameRoot = WorkspaceScanner.PathComparer.Equals(workspace.Root, root);
                if (!sameRoot || workspace.Config.ProjectId != project.Id)
                {
                    string linkedTo = workspace.Config.Project is null ? "" : $" of project '{workspace.Config.Project}'";
                    AnsiConsole.MarkupLine($"[red]'{Markup.Escape(workspace.Root)}' is already a Vertex workspace{Markup.Escape(linkedTo)}. Clone into another folder: 'vx clone {Markup.Escape(project.Name)} <folder>'.[/]");
                    return 1;
                }

                if (workspace.Config.BranchId != branch.Id)
                {
                    AnsiConsole.MarkupLine($"[red]'{Markup.Escape(workspace.Root)}' is already a workspace of {Markup.Escape(project.Name)} on branch '{Markup.Escape(workspace.Config.Branch ?? "?")}'. Run 'vx checkout {Markup.Escape(branch.Name)}' in it to switch.[/]");
                    return 1;
                }
            }
            else
            {
                workspace = VWorkspace.Initialize(root, new WorkspaceConfig
                {
                    Address = settings.ServerFromOption ? settings.ServerAddress : null,
                    Project = project.Name,
                    ProjectId = project.Id,
                    Branch = branch.Name,
                    BranchId = branch.Id,
                    User = settings.Credential?.Username
                });
            }

            LinkedWorkspace linked = LinkedWorkspace.TryFrom(workspace)!;
            using WorkspaceIndex index = workspace.OpenIndex();

            // Scan so files already in the folder are adopted or kept, never overwritten.
            WorkspaceStatus status = null!;
            AnsiConsole.Status().Start("Scanning directory...", _ =>
            {
                status = WorkspaceScanner.Scan(workspace, index, new ScanOptions { MaxConcurrency = Math.Max(1, settings.MaxConcurrency) }, cancellationToken);
            });

            SyncOutcome outcome;
            try
            {
                outcome = await WorkspaceSync.RunAsync(linked, index, status, settings, 0, cancellationToken);
            }
            catch (Exception)
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(workspace.Root)} is linked to {Markup.Escape(project.Name)}/{Markup.Escape(branch.Name)}, but not all files are downloaded. Run 'vx sync' in it to finish.[/]");
                throw;
            }

            timer.Stop();
            LockOutput.PrintError(outcome.Locks);
            if (!outcome.Changed && outcome.Conflicts.Count == 0)
            {
                AnsiConsole.MarkupLine($"[purple]{Markup.Escape(workspace.Root)}[/] already has [purple]{Markup.Escape(project.Name)}/{Markup.Escape(branch.Name)}[/] at [purple]#{outcome.Head.HeadRevision}[/].");
                return 0;
            }

            AnsiConsole.MarkupLine($"Cloned [purple]{Markup.Escape(project.Name)}/{Markup.Escape(branch.Name)}[/] at [purple]#{outcome.Head.HeadRevision}[/] into [purple]{Markup.Escape(workspace.Root)}[/]: {outcome.Updated:N0} file(s) downloaded.");
            WorkspaceSync.PrintTransfer(outcome, timer);
            WorkspaceSync.PrintConflicts(workspace, outcome.Conflicts);
            return outcome.Conflicts.Count == 0 ? 0 : 1;
        }
    }
}
