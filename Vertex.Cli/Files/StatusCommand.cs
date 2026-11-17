// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;

namespace Vertex.Cli.Files
{
    public sealed class StatusSettings : FilesSettings
    {
        [CommandOption("--limit")]
        [Description("The maximum number of changes to display. Defaults to 25.")]
        public int DisplayLimit { get; set; } = 25;
    }

    /// <summary>
    /// Lists the workspace's changes and keeps its locks in step: changed files get locked, files no longer changed
    /// are released, and files others locked become read-only. Works offline too; only the lock step is skipped.
    /// </summary>
    public sealed class StatusCommand : AsyncCommand<StatusSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, StatusSettings settings, CancellationToken cancellationToken)
        {
            Stopwatch sw = Stopwatch.StartNew();
            VWorkspace workspace = VWorkspace.FindRequired(settings.RootPath);
            using WorkspaceIndex index = workspace.OpenIndex();

            WorkspaceStatus status = null!;
            LockReport? locks = null;
            await AnsiConsole.Status().StartAsync("Scanning for changes...", async _ =>
            {
                status = WorkspaceScanner.Scan(workspace, index, new ScanOptions { MaxConcurrency = settings.MaxConcurrency, Force = settings.Force }, cancellationToken);
                if (LinkedWorkspace.TryFrom(workspace) is LinkedWorkspace linked)
                {
                    using LockInterface client = new(settings.ServerAddress, settings.Token);
                    locks = await LockOutput.Open(linked, index, client, settings).ReconcileAsync(status.Changes, ct: cancellationToken);
                }
            });

            Dictionary<string, HeldLock> held = (locks?.HeldByOthers ?? []).ToDictionary(h => RepositoryPath.ToKey(h.Path), StringComparer.Ordinal);
            foreach (WorkspaceChange change in status.Changes.Take(settings.DisplayLimit))
            {
                (string label, string color) = change.Type switch
                {
                    ChangeType.Created => ("Added    ", "green"),
                    ChangeType.Modified => ("Modified ", "yellow"),
                    ChangeType.Deleted => ("Deleted  ", "red"),
                    ChangeType.Renamed => ("Renamed  ", "cyan"),
                    _ => throw new ArgumentOutOfRangeException(nameof(change.Type), change.Type, null)
                };

                string path = Markup.Escape(change.PreviousPath is null ? change.Path : $"{change.PreviousPath} -> {change.Path}");

                // A change to a file someone else holds can't be submitted; say so on its line, with the fix.
                HeldLock? holder = HeldFor(held, change);
                AnsiConsole.MarkupLine(holder is null
                    ? $"[{color}]  {label} {path}[/]"
                    : $"[red]! {label} {path}  ({Markup.Escape(LockOutput.Describe(workspace, holder))})[/]");
            }

            int remaining = status.Changes.Count - settings.DisplayLimit;
            if (remaining > 0) AnsiConsole.MarkupLine($"[grey]  ...and {remaining:N0} more[/]");

            foreach (string path in status.Unreadable.Take(10)) AnsiConsole.MarkupLine($"[red]  unreadable {Markup.Escape(path)}[/]");
            if (status.Changes.Count == 0) AnsiConsole.MarkupLine("[grey]No changes.[/]");

            int Count(ChangeType type)
            {
                return status.Changes.Count(c => c.Type == type);
            }

            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[grey]Scanned {status.FilesScanned:N0} files, hashed {status.FilesHashed:N0} ({status.BytesHashed / (1024.0 * 1024):N1} MB) in {sw.Elapsed.TotalSeconds:N2}s.[/]");
            AnsiConsole.MarkupLine($"[grey]{Count(ChangeType.Created):N0} added, {Count(ChangeType.Modified):N0} modified, {Count(ChangeType.Deleted):N0} deleted, {Count(ChangeType.Renamed):N0} renamed.[/]");
            if (locks is null) return 0;

            if (locks.Error is null)
            {
                AnsiConsole.MarkupLine($"[grey]{locks.LockedByMe:N0} file(s) locked by you, {locks.LockedByOthers:N0} by others (read-only here).[/]");
            }

            if (locks.HeldByOthers.Count > 0)
                AnsiConsole.MarkupLine($"[red]{locks.HeldByOthers.Count:N0} changed file(s) are locked by someone else and can't be submitted.[/]");

            LockOutput.PrintError(locks);
            return 0;
        }

        private static HeldLock? HeldFor(Dictionary<string, HeldLock> held, WorkspaceChange change)
        {
            if (held.TryGetValue(RepositoryPath.ToKey(change.Path), out HeldLock? holder)) return holder;
            return change.PreviousPath is not null && held.TryGetValue(RepositoryPath.ToKey(change.PreviousPath), out holder) ? holder : null;
        }
    }
}
