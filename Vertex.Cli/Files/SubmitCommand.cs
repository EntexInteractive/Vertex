// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
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
            PathFilter filter = PathFilter.Create(linked.Workspace, settings.Paths);
            int parallel = Math.Max(1, settings.MaxConcurrency);
            using WorkspaceIndex index = linked.Workspace.OpenIndex();

            WorkspaceStatus status = null!;
            AnsiConsole.Status().Start("Scanning for changes...", _ =>
            {
                status = WorkspaceScanner.Scan(linked.Workspace, index, new ScanOptions { MaxConcurrency = parallel, KeepChunks = true }, cancellationToken);
            });

            List<WorkspaceChange> selected = status.Changes.Where(filter.Matches).ToList();
            List<string> unreadable = status.Unreadable.Where(filter.Matches).ToList();
            if (unreadable.Count > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping {unreadable.Count:N0} file(s) that can't be read, e.g. {Markup.Escape(unreadable[0])}. Close the program using them and submit again.[/]");
            }

            if (selected.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Nothing to submit.[/]");
                return 0;
            }

            // Lock every change before uploading anything, so a file someone else holds stops the submit right away.
            // The server checks again when the revision is written.
            using LockInterface lockClient = new(settings.ServerAddress, settings.Token);
            WorkspaceLocks locks = LockOutput.Open(linked, index, lockClient, settings);
            LockReport lockReport = await locks.ReconcileAsync(status.Changes, ct: cancellationToken);
            List<HeldLock> blocked = lockReport.HeldByOthers.Where(h => filter.Matches(h.Path)).ToList();
            if (blocked.Count > 0)
            {
                string more = blocked.Count > 1 ? $" (and {blocked.Count - 1:N0} more)" : "";
                AnsiConsole.MarkupLine($"[red]Can't submit '{Markup.Escape(blocked[0].Path)}'{more}: {Markup.Escape(LockOutput.Describe(linked.Workspace, blocked[0]))}.[/]");
                return 1;
            }

            // Upload content the server doesn't have yet. Files hashed by this scan already have their chunk lists;
            // others are chunked now, and only when the server is missing them.
            using FilesInterface files = new(settings.ServerAddress, settings.Token) { MaxConcurrency = parallel };
            List<WorkspaceChange> withContent = selected.Where(c => c.Type != ChangeType.Deleted).ToList();
            UploadResult? upload = null;
            await AnsiConsole.Status().StartAsync("Uploading...", async _ =>
            {
                HashSet<string> missing = await files.FindMissingFilesAsync(withContent.Select(c => c.FileId), cancellationToken);
                ChunkedFile[] chunked = withContent.Where(c => missing.Contains(c.FileId)).DistinctBy(c => c.FileId)
                    .AsParallel().WithDegreeOfParallelism(parallel).WithCancellation(cancellationToken)
                    .Select(c => Chunk(linked.Workspace, c, cancellationToken)).ToArray();

                upload = await files.UploadAsync(chunked, ct: cancellationToken);
            });

            List<FileChange> changes = new();
            foreach (WorkspaceChange change in selected)
            {
                switch (change.Type)
                {
                    case ChangeType.Created:
                        changes.Add(new FileChange { Path = change.Path, FileId = change.FileId });
                        break;
                    case ChangeType.Modified:
                        changes.Add(new FileChange { Path = change.Path, FileId = change.FileId, BaseFileId = status.Entries[change.Path].BaseId! });
                        break;
                    case ChangeType.Deleted:
                        changes.Add(new FileChange { Path = change.Path, BaseFileId = change.FileId });
                        break;
                    case ChangeType.Renamed:
                        // The server stores a move as a deletion and a creation; the content is already there.
                        changes.Add(new FileChange { Path = change.PreviousPath!, BaseFileId = change.FileId });
                        changes.Add(new FileChange { Path = change.Path, FileId = change.FileId });
                        break;
                }
            }

            using RevisionInterface revisions = new(settings.ServerAddress, settings.Token);
            long revision = await revisions.SubmitAsync(linked.ProjectId, linked.BranchId, linked.WorkspaceId, WorkspaceLocks.Host, settings.Message.Trim(), changes, cancellationToken);

            // The submitted content is now the base the next changes are measured against.
            List<IndexEntry> upserts = new();
            List<string> deletes = new();
            foreach (WorkspaceChange change in selected)
            {
                if (change.Type == ChangeType.Deleted)
                {
                    deletes.Add(change.Path);
                    continue;
                }

                if (change.Type == ChangeType.Renamed) deletes.Add(change.PreviousPath!);
                upserts.Add(status.Entries[change.Path] with { BaseId = change.FileId });
            }

            index.Apply(upserts, deletes);

            // When nobody else submitted in between, the workspace is still in step with the branch.
            long.TryParse(index.GetMeta(WorkspaceIndex.SyncedRevisionKey), out long synced);
            if (synced == revision - 1)
                index.SetMeta(WorkspaceIndex.SyncedRevisionKey, revision.ToString());

            // The server released the submitted files' locks; keep the rest locked and catch up with everyone else's.
            HashSet<WorkspaceChange> submitted = selected.ToHashSet();
            LockReport after = await locks.ReconcileAsync(status.Changes.Where(c => !submitted.Contains(c)).ToList(), ct: cancellationToken);

            timer.Stop();
            AnsiConsole.MarkupLine($"Submitted [purple]#{revision}[/] ({selected.Count:N0} file(s)).");
            LockOutput.PrintError(after);
            if (upload is not null)
            {
                AnsiConsole.MarkupLine($"[grey]Uploaded {upload.CompressedBytesUploaded / (1024.0 * 1024):N1} MB ({upload.ChunksUploaded:N0} chunks, {upload.ChunksDeduplicated:N0} already on the server) in {timer.Elapsed.TotalSeconds:N2}s.[/]");
            }

            return 0;
        }

        private static ChunkedFile Chunk(VWorkspace workspace, WorkspaceChange change, CancellationToken cancellationToken)
        {
            ChunkedFile chunked = change.Chunks ?? ContentChunker.ChunkFile(workspace.ToFullPath(change.Path), cancellationToken);
            if (chunked.FileId != change.FileId) throw new IOException($"{change.Path} changed while it was being submitted. Run 'vx submit' again.");
            return chunked;
        }
    }
}
