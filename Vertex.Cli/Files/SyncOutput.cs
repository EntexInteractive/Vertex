// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Client.Workspaces;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Prints what a sync (<c>vx sync</c>, <c>vx checkout</c>, <c>vx clone</c>) did. The sync itself is
    /// <see cref="Vertex.Core.Client.Workspaces.WorkspaceSync"/>.
    /// </summary>
    public static class SyncOutput
    {
        /// <summary>Conflicts listed before "and N more".</summary>
        private const int ListedConflicts = 10;

        /// <summary>
        /// Prints the conflicts a sync left alone and the command that resolves them.
        /// </summary>
        public static void PrintConflicts(VWorkspace workspace, IReadOnlyList<string> conflicts)
        {
            if (conflicts.Count == 0) return;
            AnsiConsole.MarkupLine($"[yellow]{conflicts.Count:N0} file(s) changed both here and on the server were left as they are:[/]");
            foreach (string path in conflicts.Take(ListedConflicts)) AnsiConsole.MarkupLine($"[yellow]  {Markup.Escape(path)}[/]");
            if (conflicts.Count > ListedConflicts) AnsiConsole.MarkupLine($"[yellow]  ...and {conflicts.Count - ListedConflicts:N0} more[/]");
            AnsiConsole.MarkupLine($"[yellow]To take the server's version, run 'vx revert {Markup.Escape(SyncCommand.CommandPath(workspace, conflicts[0]))}' (discards your change), then 'vx sync'.[/]");
        }

        /// <summary>
        /// Prints the downloaded size and time, when anything changed.
        /// </summary>
        public static void PrintTransfer(SyncOutcome outcome, Stopwatch timer)
        {
            if (outcome.Updated > 0) AnsiConsole.MarkupLine($"[grey]Synced {TransferDisplay.Bytes(outcome.Bytes)} in {timer.Elapsed.TotalSeconds:N2}s.[/]");
        }
    }
}
