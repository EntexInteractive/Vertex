// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.Client.IO;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Cli.Files
{
    public sealed class PushCommand : AsyncCommand<FilesSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, FilesSettings settings, CancellationToken cancellationToken)
        {
            string rootPath = Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
            string address = settings.ServerAddress;
            IEnumerable<string> localFiles = FileSystemUtility.IsDirectory(rootPath) ? FileSystemUtility.GetFiles(rootPath, FileSystemUtility.GetIgnoredEntries(rootPath)) : [rootPath];

            // Chunking hashes every byte, so scan files in parallel across cores.
            VFile[] files = localFiles.AsParallel().WithCancellation(cancellationToken)
                .Select(path => new VFile(path, Guid.Empty))
                .Where(file => file.TryGenerateCheckSum())
                .ToArray();

            Dictionary<string, VFile> byPath = files.ToDictionary(f => f.Fullname, StringComparer.OrdinalIgnoreCase);
            using FilesInterface client = new(address) { MaxConcurrency = settings.DegreeOfParallelism };
            PushResult result = await client.PushAsync(files.Select(f => f.Chunks!).ToArray(), chunked =>
            {
                Terminal.WriteLine($"Stored {byPath[chunked.Path].Name} ({chunked.Size:N0} bytes), id {chunked.FileId}", ConsoleColor.Green);
            }, cancellationToken);

            long scanned = files.Sum(f => f.PhysicalSize);
            Terminal.WriteLine($"{result.FilesCommitted:N0} file(s) stored, {result.FilesAlreadyStored:N0} already on server.", ConsoleColor.Gray);
            Terminal.WriteLine($"Chunks: {result.ChunksUploaded:N0} uploaded, {result.ChunksDeduplicated:N0} deduplicated. " +
                               $"Sent {result.CompressedBytesUploaded:N0} bytes for {scanned:N0} bytes of content.", ConsoleColor.Gray);
            return 0;
        }
    }
}
