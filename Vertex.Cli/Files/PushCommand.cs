// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.Client.IO;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Server;

namespace Vertex.Cli.Files
{
    public sealed class PushCommand : AsyncCommand<FilesSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, FilesSettings settings, CancellationToken cancellationToken)
        {
            string rootPath = Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
            string address = settings.ServerAddress;
            IEnumerable<string> localFiles = FileSystemUtility.IsDirectory(rootPath) ? FileSystemUtility.GetFiles(rootPath, FileSystemUtility.GetIgnoredEntries(rootPath)) : [rootPath];

            // Checksums compress each file, so hash in parallel across cores.
            VFile[] files = localFiles.AsParallel().WithCancellation(cancellationToken)
                .Select(path => new VFile(path, Guid.Empty))
                .Where(file => file.TryGenerateCheckSum())
                .ToArray();

            using FilesInterface client = new(address);
            int uploaded = await client.UploadManyAsync(files, Environment.ProcessorCount >= 8 ? 8 : 4, (file, response) =>
            {
                if (!string.Equals(response.FileId, file.CheckSum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The server did not use the file checksum as its id. Rebuild and restart the server.");

                Terminal.WriteLine($"Uploaded {response.FileName} ({response.BytesReceived:N0} bytes), id {file.CheckSum}", ConsoleColor.Green);
            }, cancellationToken);

            Terminal.WriteLine($"{uploaded:N0} uploaded, {files.Length - uploaded:N0} already on server.", ConsoleColor.Gray);
            return 0;
        }
    }
}
