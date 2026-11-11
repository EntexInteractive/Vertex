// Copyright 2026 Entex Interactive

using Spectre.Console;
using Spectre.Console.Cli;
using Vertex.Core.Net.Interfaces;
using Vertex.Cli.Net;

namespace Vertex.Cli.Files;

public sealed class PullSettings : NetworkSettings
{
    [CommandOption("--file-id")] public string? FileId { get; set; }
    [CommandOption("--path|-p")] public string? Destination { get; set; }
}

public sealed class PullCommand : AsyncCommand<PullSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PullSettings settings, CancellationToken cancellationToken)
    {
        string fileId = settings.FileId ?? throw new ArgumentException("--file-id is required.");
        string destination = Path.GetFullPath(settings.Destination ?? fileId);
        using FilesInterface client = new(settings.ServerAddress);
        PullResult result = await client.DownloadAsync(fileId, destination, cancellationToken);
        AnsiConsole.MarkupLine($"Wrote {result.Size:N0} bytes to {Markup.Escape(destination)}: " +
                               $"{result.CompressedBytesDownloaded:N0} downloaded, {result.BytesReused:N0} reused from the local copy.");
        return 0;
    }
}