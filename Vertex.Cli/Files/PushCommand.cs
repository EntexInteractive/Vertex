// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Spectre.Console.Cli;
using Vertex.Core.Client.IO;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Server;

namespace Vertex.Cli.Files;

public sealed class PushCommand : AsyncCommand<FilesSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, FilesSettings settings, CancellationToken cancellationToken)
    {
        string rootPath = Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        string address = settings.Server ?? "http://localhost:5000";
        IEnumerable<string> localFiles = FileSystemUtility.IsDirectory(rootPath) ? FileSystemUtility.GetFiles(rootPath, FileSystemUtility.GetIgnoredEntries(rootPath)) : [rootPath];

        using FilesInterface client = new(address);
        foreach (string path in localFiles)
        {
            VFile file = new(path, Guid.NewGuid());
            if (!file.TryGenerateCheckSum()) continue;

            UploadFileResponse response = await client.UploadAsync(file, cancellationToken);
            Terminal.WriteLine($"Uploaded {response.FileName} ({response.BytesReceived:N0} bytes), id {response.FileId}", ConsoleColor.Green);
        }

        await Task.CompletedTask;
        return 0;
    }
}