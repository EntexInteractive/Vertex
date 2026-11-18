// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using Vertex.Cli.Files;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Branches
{
    public sealed class CheckoutSettings : WorkspaceSettings
    {
        [CommandArgument(0, "<branch>")]
        [Description("The branch to switch the workspace to.")]
        public string Branch { get; set; } = string.Empty;
    }

    /// <summary>
    /// Switches the workspace to another branch of its project: downloads only the files that differ, removes the
    /// ones the branch doesn't have, and follows that branch from then on. Refuses while there are local changes, so
    /// nothing is lost or carried between branches.
    /// </summary>
    /// <remarks>
    /// The config is switched before the files are, with the synced revision reset to 0. If the command stops part
    /// way, the workspace is already on the new branch and <c>vx sync</c> finishes the job (a sync from revision 0
    /// lists every file and removes the rest).
    /// </remarks>
    public sealed class CheckoutCommand : AsyncCommand<CheckoutSettings>
    {
        /// <summary>Changed files listed before "and N more".</summary>
        private const int ListedChanges = 10;

        public override async Task<int> ExecuteAsync(CommandContext context, CheckoutSettings settings, CancellationToken cancellationToken)
        {
            Stopwatch timer = Stopwatch.StartNew();
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            VWorkspace workspace = linked.Workspace;
            string name = settings.Branch.Trim();

            using BranchInterface branches = new(settings.ServerAddress, settings.Token);
            ServerResult<VBranch> result = await branches.GetBranchByNameAsync(linked.ProjectId, name, cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                string project = Markup.Escape(workspace.Config.Project ?? "this project");
                AnsiConsole.MarkupLine(result.StatusCode == 404
                    ? $"[red]Project '{project}' has no branch '{Markup.Escape(name)}'. Run 'vx branch list' to see them, or 'vx branch {Markup.Escape(name)}' to create it.[/]"
                    : $"[red]Could not get the branch: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");

                return 1;
            }

            VBranch branch = result.Data!;
            if (branch.Id == linked.BranchId)
            {
                AnsiConsole.MarkupLine($"Already on [purple]{Markup.Escape(branch.Name)}[/].");
                return 0;
            }

            using WorkspaceIndex index = workspace.OpenIndex();
            WorkspaceStatus status = null!;
            AnsiConsole.Status().Start("Scanning for changes...", _ =>
            {
                status = WorkspaceScanner.Scan(workspace, index, new ScanOptions { MaxConcurrency = Math.Max(1, settings.MaxConcurrency) }, cancellationToken);
            });

            if (status.Changes.Count > 0)
            {
                AnsiConsole.MarkupLine($"[red]{status.Changes.Count:N0} file(s) changed here. Submit them ('vx submit -m \"...\"') or discard them ('vx revert .') before switching branches.[/]");
                foreach (WorkspaceChange change in status.Changes.Take(ListedChanges))
                    AnsiConsole.MarkupLine($"[red]  {Markup.Escape(SyncCommand.CommandPath(workspace, change.Path))}[/]");
                if (status.Changes.Count > ListedChanges) AnsiConsole.MarkupLine($"[red]  ...and {status.Changes.Count - ListedChanges:N0} more[/]");
                return 1;
            }

            // The old branch's locks don't apply on the new one; the sync's lock step loads the new branch's.
            WorkspaceLocks.ForgetBranch(workspace, index);
            index.SetMeta(WorkspaceIndex.SyncedRevisionKey, "0");
            VWorkspace switched = workspace.SwitchBranch(branch.Name, branch.Id);
            LinkedWorkspace moved = linked with { Workspace = switched, BranchId = branch.Id };

            SyncOutcome outcome;
            try
            {
                outcome = await WorkspaceSync.RunAsync(moved, index, status, settings, 0, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[yellow]The workspace is on [purple]{Markup.Escape(branch.Name)}[/] now, but its files aren't all updated yet. Run 'vx sync' to finish.[/]");
                throw;
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[yellow]Stopped. The workspace is on [purple]{Markup.Escape(branch.Name)}[/] now; run 'vx sync' to finish updating its files.[/]");
                throw;
            }

            timer.Stop();
            LockOutput.PrintError(outcome.Locks);
            AnsiConsole.MarkupLine($"Switched to [purple]{Markup.Escape(branch.Name)}[/] at [purple]#{outcome.Head.HeadRevision}[/]: {outcome.Updated:N0} updated, {outcome.Deleted:N0} deleted.");
            WorkspaceSync.PrintTransfer(outcome, timer);
            WorkspaceSync.PrintConflicts(switched, outcome.Conflicts);
            return outcome.Conflicts.Count == 0 ? 0 : 1;
        }
    }
}
