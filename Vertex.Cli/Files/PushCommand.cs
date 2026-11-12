// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Uploads the workspace's added and modified files and records them as the new base in the index.
    /// </summary>
    /// <remarks>
    /// The server only stores content for now. Deletions and moves are recorded in the local index but not sent
    /// anywhere until the server tracks revisions.
    /// </remarks>
    public sealed class PushCommand : AsyncCommand<FilesSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, FilesSettings settings, CancellationToken cancellationToken)
        {
            Workspace workspace = Workspace.FindRequired(settings.RootPath);
            using WorkspaceIndex index = workspace.OpenIndex();

            WorkspaceStatus status = WorkspaceScanner.Scan(workspace, index, new ScanOptions
            {
                MaxConcurrency = settings.MaxConcurrency, Force = settings.Force, KeepChunks = true
            }, cancellationToken);

            foreach (string path in status.Unreadable)
                Terminal.WriteLine($"Skipped {path}: file could not be read.", ConsoleColor.Yellow);

            if (status.Changes.Count == 0)
            {
                Terminal.WriteLine("Nothing to push.", ConsoleColor.Gray);
                return 0;
            }

            // Files the scan didn't hash (their content id came from the index) still need chunking to upload.
            WorkspaceChange[] uploads = status.Changes.Where(c => c.Type is ChangeType.Created or ChangeType.Modified).ToArray();
            ChunkedFile[] chunked = uploads.AsParallel().WithDegreeOfParallelism(Math.Max(1, settings.MaxConcurrency)).WithCancellation(cancellationToken)
                .Select(c => c.Chunks ?? ContentChunker.ChunkFile(workspace.ToFullPath(c.Path), cancellationToken))
                .ToArray();

            UploadResult? result = null;
            if (chunked.Length > 0)
            {
                using FilesInterface client = new(settings.ServerAddress) { MaxConcurrency = settings.MaxConcurrency };
                result = await client.UploadAsync(chunked, file =>
                {
                    Terminal.WriteLine($"Stored {workspace.ToRelativePath(file.Path)} ({file.Size:N0} bytes)", ConsoleColor.Green);
                }, cancellationToken);
            }

            // Record what the server now has. The id that was uploaded wins over the scanned one, in case the file
            // changed in between; the next scan re-hashes it because its timestamp moved.
            List<IndexEntry> upserts = new();
            List<string> deletes = new();
            for (int i = 0; i < uploads.Length; i++)
                upserts.Add(status.Entries[uploads[i].Path] with { BaseId = chunked[i].FileId });

            foreach (WorkspaceChange change in status.Changes)
            {
                if (change.Type == ChangeType.Renamed)
                {
                    upserts.Add(status.Entries[change.Path] with { BaseId = change.FileId });
                    deletes.Add(change.PreviousPath!);
                }
                else if (change.Type == ChangeType.Deleted)
                {
                    deletes.Add(change.Path);
                }
            }

            index.Apply(upserts, deletes);

            int moved = status.Changes.Count(c => c.Type == ChangeType.Renamed);
            int deleted = status.Changes.Count(c => c.Type == ChangeType.Deleted);
            Terminal.WriteLine($"{uploads.Length:N0} file(s) pushed, {moved:N0} moved, {deleted:N0} deleted.", ConsoleColor.Gray);
            if (result is not null)
            {
                Terminal.WriteLine($"Chunks: {result.ChunksUploaded:N0} uploaded, {result.ChunksDeduplicated:N0} deduplicated. " +
                                   $"Sent {result.CompressedBytesUploaded:N0} bytes for {chunked.Sum(c => c.Size):N0} bytes of content.", ConsoleColor.Gray);
            }

            return 0;
        }
    }
}
