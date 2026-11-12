// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;

namespace Vertex.Cli.Files
{
    public sealed class StatusCommand : Command<FilesSettings>
    {
        private const int MaximumDisplayed = 50;

        public override int Execute(CommandContext context, FilesSettings settings, CancellationToken cancellationToken)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Workspace workspace = Workspace.FindRequired(settings.RootPath);
            using WorkspaceIndex index = workspace.OpenIndex();

            WorkspaceStatus status = null!;
            AnsiConsole.Status().Start("Scanning for changes...", _ =>
            {
                status = WorkspaceScanner.Scan(workspace, index, new ScanOptions { MaxConcurrency = settings.MaxConcurrency, Force = settings.Force }, cancellationToken);
            });

            foreach (WorkspaceChange change in status.Changes.Take(MaximumDisplayed))
            {
                string path = Markup.Escape(change.Path);
                AnsiConsole.MarkupLine(change.Type switch
                {
                    ChangeType.Created => $"[green]  added     {path}[/]",
                    ChangeType.Modified => $"[yellow]  modified  {path}[/]",
                    ChangeType.Deleted => $"[red]  deleted   {path}[/]",
                    ChangeType.Moved => $"[cyan]  moved     {Markup.Escape(change.PreviousPath!)} -> {path}[/]",
                    _ => throw new ArgumentOutOfRangeException(nameof(change.Type), change.Type, null)
                });
            }

            int remaining = status.Changes.Count - MaximumDisplayed;
            if (remaining > 0) AnsiConsole.MarkupLine($"[grey]  ...and {remaining:N0} more[/]");

            foreach (string path in status.Unreadable.Take(10)) AnsiConsole.MarkupLine($"[red]  unreadable {Markup.Escape(path)}[/]");
            if (status.Changes.Count == 0) AnsiConsole.MarkupLine("[grey]No changes.[/]");

            int Count(ChangeType type)
            {
                return status.Changes.Count(c => c.Type == type);
            }

            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[grey]Scanned {status.FilesScanned:N0} files, hashed {status.FilesHashed:N0} ({status.BytesHashed / (1024.0 * 1024):N1} MB) in {sw.Elapsed.TotalSeconds:N2}s.[/]");
            AnsiConsole.MarkupLine($"[grey]{Count(ChangeType.Created):N0} added, {Count(ChangeType.Modified):N0} modified, {Count(ChangeType.Deleted):N0} deleted, {Count(ChangeType.Moved):N0} moved.[/]");
            return 0;
        }
    }
}
