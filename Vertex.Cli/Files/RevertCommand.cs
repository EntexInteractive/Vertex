// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Core.Client.IO;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;

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
            int parallel = Math.Max(1, settings.MaxConcurrency);
            using WorkspaceIndex index = workspace.OpenIndex();

            WorkspaceStatus status = null!;
            AnsiConsole.Status().Start("Scanning for changes...", _ =>
            {
                status = WorkspaceScanner.Scan(workspace, index, new ScanOptions { MaxConcurrency = parallel }, cancellationToken);
            });

            List<WorkspaceChange> selected = status.Changes.Where(filter.Matches).ToList();
            List<WorkspaceChange> added = selected.Where(c => c.Type == ChangeType.Created).ToList();
            List<WorkspaceChange> restore = selected.Where(c => c.Type != ChangeType.Created).ToList();
            if (selected.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Nothing to revert.[/]");
                return 0;
            }

            // Each restored file goes back to its base content, at its old path for a move.
            List<(string Path, string FileId)> downloads = restore.Select(c => (c.PreviousPath ?? c.Path, c.Type == ChangeType.Modified ? status.Entries[c.Path].BaseId! : c.FileId)).ToList();
            List<IndexEntry> upserts = new();
            List<string> deletes = new();
            using (FilesInterface files = new(settings.ServerAddress, settings.Token) { MaxConcurrency = 2 })
            {
                object gate = new();
                await AnsiConsole.Status().StartAsync("Reverting...", async _ =>
                {
                    await Parallel.ForEachAsync(downloads, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (file, ct) =>
                    {
                        string fullPath = workspace.ToFullPath(file.Path);
                        await files.DownloadAsync(file.FileId, fullPath, ct);
                        FileInfo info = new(fullPath);
                        lock (gate) upserts.Add(new IndexEntry(file.Path, info.Length, info.LastWriteTimeUtc.Ticks, file.FileId, file.FileId, DateTime.UtcNow.Ticks));
                    });
                });
            }

            // The new path of a move holds the same content that was just restored at the old path, so removing it
            // loses nothing.
            foreach (WorkspaceChange move in restore.Where(c => c.Type == ChangeType.Renamed))
            {
                string fullPath = workspace.ToFullPath(move.Path);
                FileSystemUtility.MakeWritable(fullPath);
                File.Delete(fullPath);
                deletes.Add(move.Path);
            }

            index.Apply(upserts, deletes);
            if (restore.Count > 0) AnsiConsole.MarkupLine($"Reverted {restore.Count:N0} file(s).");

            if (added.Count > 0)
            {
                string example = SyncCommand.CommandPath(workspace, added[0].Path);
                AnsiConsole.MarkupLine($"[yellow]{added.Count:N0} new file(s) left as they are, e.g. {Markup.Escape(example)}: the server has no earlier version. Delete them, or add them to .vxignore to stop tracking them.[/]");
            }

            return 0;
        }
    }
}
