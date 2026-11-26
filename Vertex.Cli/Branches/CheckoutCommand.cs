// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
            CheckoutOutcome checkout;
            try
            {
                checkout = await WorkspaceCheckout.RunAsync(linked, settings.Connection, branch, ConsoleReporter.Instance, cancellationToken);
            }
            catch (CheckoutIncompleteException ex) when (ex.InnerException is OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[yellow]Stopped. The workspace is on [purple]{Markup.Escape(branch.Name)}[/] now; run 'vx sync' to finish updating its files.[/]");
                ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
            catch (CheckoutIncompleteException ex)
            {
                AnsiConsole.MarkupLine($"[yellow]The workspace is on [purple]{Markup.Escape(branch.Name)}[/] now, but its files aren't all updated yet. Run 'vx sync' to finish.[/]");
                ExceptionDispatchInfo.Throw(ex.InnerException!);
                throw;
            }

            if (checkout.AlreadyOnBranch)
            {
                AnsiConsole.MarkupLine($"Already on [purple]{Markup.Escape(branch.Name)}[/].");
                return 0;
            }

            IReadOnlyList<WorkspaceChange> changes = checkout.LocalChanges;
            if (changes.Count > 0)
            {
                AnsiConsole.MarkupLine($"[red]{changes.Count:N0} file(s) changed here. Submit them ('vx submit -m \"...\"') or discard them ('vx revert .') before switching branches.[/]");
                foreach (WorkspaceChange change in changes.Take(ListedChanges))
                    AnsiConsole.MarkupLine($"[red]  {Markup.Escape(SyncCommand.CommandPath(workspace, change.Path))}[/]");
                if (changes.Count > ListedChanges) AnsiConsole.MarkupLine($"[red]  ...and {changes.Count - ListedChanges:N0} more[/]");
                return 1;
            }

            SyncOutcome outcome = checkout.Sync!;
            VWorkspace switched = checkout.Workspace.Workspace;
            timer.Stop();
            LockOutput.PrintError(outcome.Locks);
            AnsiConsole.MarkupLine($"Switched to [purple]{Markup.Escape(branch.Name)}[/] at [purple]#{outcome.Head.HeadRevision}[/]: {outcome.Updated:N0} updated, {outcome.Deleted:N0} deleted.");
            SyncOutput.PrintTransfer(outcome, timer);
            SyncOutput.PrintConflicts(switched, outcome.Conflicts);
            return outcome.Conflicts.Count == 0 ? 0 : 1;
        }
    }
}
