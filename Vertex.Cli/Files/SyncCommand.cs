// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Cli.Files
{
    public sealed class SyncSettings : WorkspaceSettings
    {
        [CommandOption("--revision|-r <REVISION>")]
        [Description("The revision to sync to, e.g. 120 or #120. Defaults to the branch's latest.")]
        public string? Revision { get; set; }

        public override ValidationResult Validate()
        {
            return Revision is null || TryParseRevision(Revision, out _)
                ? ValidationResult.Success()
                : ValidationResult.Error($"'{Revision}' isn't a revision. Use a number, e.g. 'vx sync -r 120'.");
        }

        /// <summary>
        /// The revision asked for, or 0 for the latest.
        /// </summary>
        public long TargetRevision => Revision is null ? 0 : TryParseRevision(Revision, out long revision) ? revision : 0;

        private static bool TryParseRevision(string text, out long revision)
        {
            return long.TryParse(text.Trim().TrimStart('#'), out revision) && revision > 0;
        }
    }

    /// <summary>
    /// Brings the workspace to the latest revision of its branch, or with <c>-r</c> to an older (or newer) one:
    /// downloads files that differ and removes files that don't exist there. Files changed locally are never
    /// overwritten; they are reported as conflicts with the command that resolves them.
    /// </summary>
    public sealed class SyncCommand : AsyncCommand<SyncSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, SyncSettings settings, CancellationToken cancellationToken)
        {
            Stopwatch timer = Stopwatch.StartNew();
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            using WorkspaceIndex index = linked.Workspace.OpenIndex();

            // Scan first so local edits are known and never overwritten.
            WorkspaceStatus status = null!;
            AnsiConsole.Status().Start("Scanning...", _ =>
            {
                status = WorkspaceScanner.Scan(linked.Workspace, index, new ScanOptions { MaxConcurrency = Math.Max(1, settings.MaxConcurrency) }, cancellationToken);
            });

            SyncOutcome outcome = await WorkspaceSync.RunAsync(linked, index, status, settings, settings.TargetRevision, cancellationToken);
            timer.Stop();

            LockOutput.PrintError(outcome.Locks);
            HeadChanges head = outcome.Head;
            string behind = head.HeadRevision < head.LatestRevision ? $" [grey](latest is #{head.LatestRevision})[/]" : "";
            if (!outcome.Changed && outcome.Conflicts.Count == 0)
            {
                AnsiConsole.MarkupLine($"Already up to date at [purple]#{head.HeadRevision}[/]{behind}.");
                return 0;
            }

            AnsiConsole.MarkupLine($"Synced to [purple]#{head.HeadRevision}[/]{behind}: {outcome.Updated:N0} updated, {outcome.Deleted:N0} deleted.");
            WorkspaceSync.PrintTransfer(outcome, timer);
            WorkspaceSync.PrintConflicts(linked.Workspace, outcome.Conflicts);
            return outcome.Conflicts.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// A workspace path as the user would type it from the current folder.
        /// </summary>
        public static string CommandPath(VWorkspace workspace, string path)
        {
            string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), workspace.ToFullPath(path)).Replace(Path.DirectorySeparatorChar, '/');
            return relative.Contains(' ') ? $"\"{relative}\"" : relative;
        }
    }
}
