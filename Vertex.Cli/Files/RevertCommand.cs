// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Core.Client.Workspaces;

namespace Vertex.Cli.Files
{
    public sealed class RevertSettings : WorkspaceSettings
    {
        [CommandArgument(0, "<paths>")]
        [Description("Files or folders to revert. Use '.' for every change in the current folder.")]
        public string[] Paths { get; set; } = [];
    }

    /// <summary>
    /// Discards local changes: modified and deleted files get the content they had at the last submit or sync back
    /// from the server, and moved files go back to where they were. New files are left alone, because the server has
    /// no copy to restore.
    /// </summary>
    public sealed class RevertCommand : AsyncCommand<RevertSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, RevertSettings settings, CancellationToken cancellationToken)
        {
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            VWorkspace workspace = linked.Workspace;
            PathFilter filter = PathFilter.Create(workspace, settings.Paths);
            RevertOutcome outcome = await WorkspaceRevert.RunAsync(linked, settings.Connection, filter, ConsoleReporter.Instance, cancellationToken);
            if (outcome.NothingToRevert)
            {
                AnsiConsole.MarkupLine("[grey]Nothing to revert.[/]");
                return 0;
            }

            if (outcome.Reverted > 0) AnsiConsole.MarkupLine($"Reverted {outcome.Reverted:N0} file(s).");
            if (outcome.Locks is not null) LockOutput.PrintError(outcome.Locks);

            IReadOnlyList<string> added = outcome.LeftAdded;
            if (added.Count > 0)
            {
                string example = SyncCommand.CommandPath(workspace, added[0]);
                AnsiConsole.MarkupLine($"[yellow]{added.Count:N0} new file(s) left as they are, e.g. {Markup.Escape(example)}: the server has no earlier version. Delete them, or add them to .vxignore to stop tracking them.[/]");
            }

            return 0;
        }
    }
}
