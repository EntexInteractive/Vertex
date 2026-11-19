// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Files;
using Vertex.Cli.Net;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;
using Vertex.Server;

namespace Vertex.Cli.Branches
{
    public sealed class MergeSettings : WorkspaceSettings
    {
        [CommandArgument(0, "<source>")]
        [Description("The branch whose changes to take in.")]
        public string Source { get; set; } = string.Empty;

        [CommandOption("--into")]
        [Description("The branch to merge into. Defaults to the workspace's branch.")]
        public string? Into { get; set; }

        [CommandOption("--message|-m")]
        [Description("Describes the merge revision. Defaults to \"Merged <source> into <target>\".")]
        public string? Message { get; set; }

        [CommandOption("--take-source")]
        [Description("For files changed on both branches, use the source branch's version.")]
        public bool TakeSource { get; set; }

        [CommandOption("--keep-target")]
        [Description("For files changed on both branches, keep the target branch's version.")]
        public bool KeepTarget { get; set; }
    }

    /// <summary>
    /// Merges another branch into the workspace's branch (or into <c>--into</c>): every file changed on the source
    /// since the last merge between the two, as one revision on the target. Happens entirely on the server, so nothing
    /// is downloaded. Files changed on both branches stop the merge unless <c>--take-source</c> or
    /// <c>--keep-target</c> says which side wins.
    /// </summary>
    public sealed class MergeCommand : AsyncCommand<MergeSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, MergeSettings settings, CancellationToken cancellationToken)
        {
            if (settings.TakeSource && settings.KeepTarget)
            {
                AnsiConsole.MarkupLine("[red]Pick one: --take-source or --keep-target.[/]");
                return 1;
            }

            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            using BranchInterface branches = new(settings.ServerAddress, settings.Token);
            VBranch? source = await MergeOutput.FindBranchAsync(branches, linked, settings.Source, cancellationToken);
            if (source is null) return 1;

            // The workspace's own branch by default; the server checks it still exists.
            string? into = string.IsNullOrWhiteSpace(settings.Into) ? null : settings.Into;
            VBranch? target = into is null
                ? new VBranch(linked.BranchId, 0, linked.Workspace.Config.Branch ?? "this branch", linked.ProjectId, null)
                : await MergeOutput.FindBranchAsync(branches, linked, into, cancellationToken);
            if (target is null) return 1;

            if (source.Id == target.Id)
            {
                AnsiConsole.MarkupLine($"[red]The workspace is on '{Markup.Escape(target.Name)}' already. Name the branch to merge in, or use --into: vx merge {Markup.Escape(source.Name)} --into <branch>.[/]");
                return 1;
            }

            MergeResolution resolve = settings.TakeSource ? MergeResolution.TakeSource : settings.KeepTarget ? MergeResolution.KeepTarget : MergeResolution.None;
            bool merged = await MergeOutput.RunAsync(settings, linked, source, target, settings.Message?.Trim() ?? string.Empty, resolve, cancellationToken);
            return merged ? 0 : 1;
        }
    }

    /// <summary>
    /// Runs a merge and prints the outcome, for <c>vx merge</c> and <c>vx submit --merge-to</c>.
    /// </summary>
    internal static class MergeOutput
    {
        /// <summary>Conflicting files listed before "and N more".</summary>
        private const int ListedConflicts = 10;

        /// <summary>
        /// Looks up a branch of the workspace's project by name, printing how to fix it when there's none.
        /// </summary>
        public static async Task<VBranch?> FindBranchAsync(BranchInterface branches, LinkedWorkspace linked, string name, CancellationToken cancellationToken)
        {
            name = name.Trim();
            ServerResult<VBranch> result = await branches.GetBranchByNameAsync(linked.ProjectId, name, cancellationToken);
            if (result.IsSuccessStatusCode) return result.Data!;

            string project = Markup.Escape(linked.Workspace.Config.Project ?? "this project");
            AnsiConsole.MarkupLine(result.StatusCode == 404
                ? $"[red]Project '{project}' has no branch '{Markup.Escape(name)}'. Run 'vx branch list' to see them.[/]"
                : $"[red]Could not get the branch: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
            return null;
        }

        /// <returns>False when conflicts stopped the merge.</returns>
        public static async Task<bool> RunAsync(NetworkSettings settings, LinkedWorkspace linked, VBranch source, VBranch target, string message, MergeResolution resolve,
            CancellationToken cancellationToken)
        {
            using RevisionInterface revisions = new(settings.ServerAddress, settings.Token);
            MergeResponse response = null!;
            await AnsiConsole.Status().StartAsync($"Merging {Markup.Escape(source.Name)} into {Markup.Escape(target.Name)}...", async _ =>
            {
                response = await revisions.MergeAsync(linked.ProjectId, source.Id, target.Id, message, resolve, linked.WorkspaceId, WorkspaceLocks.Host, cancellationToken);
            });

            string from = Markup.Escape(source.Name);
            string into = Markup.Escape(target.Name);
            if (response.Conflicts.Count > 0)
            {
                int total = Math.Max(response.ConflictsTotal, response.Conflicts.Count);
                AnsiConsole.MarkupLine($"[red]Nothing was merged: {total:N0} file(s) changed on both {from} and {into}.[/]");
                foreach (MergeConflict conflict in response.Conflicts.Take(ListedConflicts)) AnsiConsole.MarkupLine($"[red]  {Markup.Escape(conflict.Path)}{Describe(conflict)}[/]");
                if (total > ListedConflicts) AnsiConsole.MarkupLine($"[red]  ...and {total - ListedConflicts:N0} more[/]");
                AnsiConsole.MarkupLine($"Use {from}'s versions with 'vx merge {from} --into {into} --take-source', or keep {into}'s with '--keep-target'. To combine them by hand, edit the files on {into}, submit, then merge with '--keep-target'.");
                return false;
            }

            if (response.Revision == 0)
            {
                AnsiConsole.MarkupLine(response.Resolved > 0
                    ? $"Merged {from} into {into}: {response.Resolved:N0} conflict(s) kept {into}'s version, nothing else to change."
                    : $"[grey]{into} already has every change from {from}.[/]");
                return true;
            }

            string resolved = response.Resolved > 0 ? $", {response.Resolved:N0} conflict(s) settled with {(resolve == MergeResolution.TakeSource ? from + "'s" : into + "'s")} version" : "";
            AnsiConsole.MarkupLine($"Merged {from} into {into} as [purple]#{response.Revision}[/] ({response.Files:N0} file(s){resolved}).");
            PrintAutoMerges(response.AutoMerges);
            if (target.Id == linked.BranchId) AnsiConsole.MarkupLine("[grey]Run 'vx sync' to get the merged files.[/]");
            return true;
        }

        /// <summary>
        /// Prints what a branch's merge chain did after a submit or merge: one line for the hops that merged, then why
        /// it stopped, if it stopped early, with the command that carries it on.
        /// </summary>
        /// <returns>False when the chain stopped on a conflict or error (the revision that started it still stands).</returns>
        public static bool PrintAutoMerges(IReadOnlyList<AutoMergeResult> hops)
        {
            List<string> merged = hops.Where(h => h.Revision > 0).Select(h => $"into [purple]{Markup.Escape(h.TargetBranch)}[/] as [purple]#{h.Revision}[/]").ToList();
            if (merged.Count > 0) AnsiConsole.MarkupLine($"Auto-merged {string.Join(", ", merged)}.");

            AutoMergeResult? last = hops.Count > 0 ? hops[^1] : null;
            if (last is null || last.Revision > 0) return true;

            string from = Markup.Escape(last.SourceBranch);
            string into = Markup.Escape(last.TargetBranch);
            if (last.Conflicts.Count > 0)
            {
                int total = Math.Max(last.ConflictsTotal, last.Conflicts.Count);
                AnsiConsole.MarkupLine($"[yellow]Auto-merge from {from} into {into} stopped: {total:N0} file(s) changed on both.[/]");
                foreach (MergeConflict conflict in last.Conflicts.Take(ListedConflicts)) AnsiConsole.MarkupLine($"[yellow]  {Markup.Escape(conflict.Path)}{Describe(conflict)}[/]");
                if (total > ListedConflicts) AnsiConsole.MarkupLine($"[yellow]  ...and {total - ListedConflicts:N0} more[/]");
                AnsiConsole.MarkupLine($"Settle it with 'vx merge {from} --into {into} --take-source' (use {from}'s) or '--keep-target' (keep {into}'s); the chain carries on from there.");
                return false;
            }

            if (last.Error.Length > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Auto-merge from {from} into {into} stopped: {Markup.Escape(last.Error)}[/]");
                return false;
            }

            if (merged.Count == 0) AnsiConsole.MarkupLine($"[grey]{into} already has it (auto-merge).[/]");
            return true;
        }

        private static string Describe(MergeConflict conflict)
        {
            if (conflict.SourceFileId.Length == 0) return " (deleted on the source, changed on the target)";
            if (conflict.TargetFileId.Length == 0) return " (changed on the source, deleted on the target)";
            return "";
        }
    }
}
