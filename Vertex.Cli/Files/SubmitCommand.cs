// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using Vertex.Cli.Branches;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Server;

namespace Vertex.Cli.Files
{
    public sealed class SubmitSettings : WorkspaceSettings
    {
        [CommandArgument(0, "[paths]")]
        [Description("Files or folders to submit. Defaults to every change in the current folder.")]
        public string[] Paths { get; set; } = [];

        [CommandOption("--message|-m")]
        [Description("What changed, shown in the project's history.")]
        public string? Message { get; set; }

        [CommandOption("--merge-to")]
        [Description("After submitting, also merge the branch into this one. Files changed on both stop the merge, not the submit.")]
        public string? MergeTo { get; set; }
    }

    /// <summary>
    /// Submits the workspace's changes as one revision: every added, modified, deleted and moved file in the current
    /// folder (or the named paths), the way <c>vx status</c> lists them. Content the server already stores is not sent
    /// again.
    /// </summary>
    public sealed class SubmitCommand : AsyncCommand<SubmitSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, SubmitSettings settings, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(settings.Message))
            {
                AnsiConsole.MarkupLine("[red]Describe the revision: vx submit -m \"What changed\"[/]");
                return 1;
            }

            Stopwatch timer = Stopwatch.StartNew();
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();

            // Check the merge target before submitting anything, so a typo doesn't leave a half-done job.
            VBranch? mergeTarget = null;
            if (!string.IsNullOrWhiteSpace(settings.MergeTo))
            {
                using BranchInterface branches = new(settings.ServerAddress, settings.Token);
                mergeTarget = await MergeOutput.FindBranchAsync(branches, linked, settings.MergeTo, cancellationToken);
                if (mergeTarget is null) return 1;
                if (mergeTarget.Id == linked.BranchId)
                {
                    AnsiConsole.MarkupLine($"[red]The workspace is on '{Markup.Escape(mergeTarget.Name)}' already. Name another branch for --merge-to, or leave it out.[/]");
                    return 1;
                }
            }

            PathFilter filter = PathFilter.Create(linked.Workspace, settings.Paths);
            SubmitOutcome outcome = await WorkspaceSubmit.RunAsync(linked, settings.Connection, filter, settings.Message, ConsoleReporter.Instance, cancellationToken);

            if (outcome.Unreadable.Count > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping {outcome.Unreadable.Count:N0} file(s) that can't be read, e.g. {Markup.Escape(outcome.Unreadable[0])}. Close the program using them and submit again.[/]");
            }

            switch (outcome.Result)
            {
                case SubmitResult.NothingToSubmit:
                    AnsiConsole.MarkupLine("[grey]Nothing to submit.[/]");
                    return 0;
                case SubmitResult.Blocked:
                    HeldLock first = outcome.Blocked[0];
                    string more = outcome.Blocked.Count > 1 ? $" (and {outcome.Blocked.Count - 1:N0} more)" : "";
                    AnsiConsole.MarkupLine($"[red]Can't submit '{Markup.Escape(first.Path)}'{more}: {Markup.Escape(LockOutput.Describe(linked.Workspace, first))}.[/]");
                    return 1;
            }

            timer.Stop();
            AnsiConsole.MarkupLine($"Submitted [purple]#{outcome.Revision}[/] ({outcome.Files:N0} file(s)).");
            LockOutput.PrintError(outcome.Locks!);
            if (outcome.Upload is UploadResult upload)
            {
                AnsiConsole.MarkupLine($"[grey]Submitted {TransferDisplay.Bytes(upload.CompressedBytesUploaded)} ({upload.ChunksUploaded:N0} chunks, {upload.ChunksDeduplicated:N0} already on the server) in {timer.Elapsed.TotalSeconds:N2}s.[/]");
            }

            // The branch's merge chain (vx branch --merge-to) ran on the server; a stopped chain warns but the submit stands.
            MergeOutput.PrintAutoMerges(outcome.Response!.AutoMerges);

            if (mergeTarget is not null)
            {
                // The submit stands either way; a refused merge says how to finish it with 'vx merge'.
                VBranch source = new(linked.BranchId, 0, linked.Workspace.Config.Branch ?? "this branch", linked.ProjectId, null);
                bool merged = await MergeOutput.RunAsync(settings, linked, source, mergeTarget, settings.Message.Trim(), MergeResolution.None, cancellationToken);
                if (!merged) return 1;
            }

            return 0;
        }
    }
}
