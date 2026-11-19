// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Cli.Files
{
    public sealed class LocksSettings : WorkspaceSettings
    {
        [CommandArgument(0, "[paths]")]
        [Description("Files or folders to show locks for. Defaults to the whole workspace.")]
        public string[] Paths { get; set; } = [];
    }

    /// <summary>
    /// Lists who has which files locked on the workspace's branch.
    /// </summary>
    public sealed class LocksCommand : AsyncCommand<LocksSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, LocksSettings settings, CancellationToken cancellationToken)
        {
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            PathFilter filter = PathFilter.Create(linked.Workspace, settings.Paths.Length > 0 ? settings.Paths : [linked.Workspace.Root]);
            using WorkspaceIndex index = linked.Workspace.OpenIndex();
            using LockInterface client = new(settings.ServerAddress, settings.Token);
            WorkspaceLocks locks = LockOutput.Open(linked, index, client, settings);

            LockReport report = await locks.RefreshAsync(cancellationToken);
            LockOutput.PrintError(report);

            List<LockEntry> shown = locks.Current().Where(l => filter.Matches(l.Path)).ToList();
            if (shown.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No files are locked.[/]");
                return 0;
            }

            Table table = new Table().Border(TableBorder.Simple).AddColumns("File", "Locked by", "Machine", "Since");
            foreach (LockEntry entry in shown)
            {
                string since = DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAt).ToLocalTime().ToString("g");
                string who = entry.Mine ? "[purple]you (this workspace)[/]" : Markup.Escape(entry.Username);
                table.AddRow(Markup.Escape(entry.Path), who, Markup.Escape(entry.Host), since);
            }

            AnsiConsole.Write(table);
            return 0;
        }
    }

    public sealed class UnlockSettings : WorkspaceSettings
    {
        [CommandArgument(0, "<paths>")]
        [Description("Files or folders whose locks to release.")]
        public string[] Paths { get; set; } = [];
    }

    /// <summary>
    /// Releases locks held somewhere else: your own from a workspace that's gone, or anyone's with manage access.
    /// This workspace's own locks are released by submitting or reverting the files.
    /// </summary>
    public sealed class UnlockCommand : AsyncCommand<UnlockSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, UnlockSettings settings, CancellationToken cancellationToken)
        {
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            PathFilter filter = PathFilter.Create(linked.Workspace, settings.Paths);
            using WorkspaceIndex index = linked.Workspace.OpenIndex();
            using LockInterface client = new(settings.ServerAddress, settings.Token);
            WorkspaceLocks locks = LockOutput.Open(linked, index, client, settings);

            // Work from the server's current list, not a stale one.
            LockReport report = await locks.RefreshAsync(cancellationToken);
            if (report.Error is not null) throw report.Error;

            List<LockEntry> matching = locks.Current().Where(l => filter.Matches(l.Path)).ToList();
            List<LockEntry> elsewhere = matching.Where(l => !l.Mine).ToList();
            if (elsewhere.Count == 0)
            {
                AnsiConsole.MarkupLine(matching.Count > 0
                    ? $"[grey]'{Markup.Escape(matching[0].Path)}' is locked by this workspace. Its lock is released when you submit it, or run 'vx revert {Markup.Escape(SyncCommand.CommandPath(linked.Workspace, matching[0].Path))}' to drop your change.[/]"
                    : "[grey]No locks on those files.[/]");

                return 0;
            }

            HashSet<string> released = (await client.UnlockAsync(linked.ProjectId, linked.BranchId, linked.WorkspaceId, elsewhere.Select(l => l.Path).ToList(), force: true, cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (LockEntry entry in elsewhere.Where(l => released.Contains(l.Path)))
                AnsiConsole.MarkupLine($"Released [purple]{Markup.Escape(entry.Path)}[/] (was locked by {Markup.Escape(entry.Username)} on {Markup.Escape(entry.Host)}).");

            List<LockEntry> refused = elsewhere.Where(l => !released.Contains(l.Path)).ToList();
            if (refused.Count > 0)
            {
                AnsiConsole.MarkupLine($"[red]Couldn't release {refused.Count:N0} lock(s), e.g. '{Markup.Escape(refused[0].Path)}' held by {Markup.Escape(refused[0].Username)}: " +
                                       "only people with manage access can release other people's locks. Ask them to submit or revert it, or ask someone with manage access.[/]");
            }

            LockOutput.PrintError(await locks.RefreshAsync(cancellationToken));
            return refused.Count > 0 ? 1 : 0;
        }
    }
}
