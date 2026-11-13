// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using EntexInteractive.Extensions;
using Vertex.Cli.Net;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Cli.Files
{
    public sealed class PullSettings : NetworkSettings
    {
        [CommandArgument(0, "<file-id>")]
        [Description("The id of the file to download, as printed by 'vx push'.")]
        public string FileId { get; set; } = "";

        [CommandArgument(1, "[destination]")]
        [Description("Where to write the file. A folder keeps the file id as the name. Defaults to the current folder.")]
        public string? Destination { get; set; }

        [CommandOption("--parallel")]
        [Description("The number of parallel operations to perform.")]
        public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
    }

    /// <summary>
    /// Test command: downloads one file by id from the server's content store, without a workspace or index.
    /// <c>vx sync</c> replaces this.
    /// </summary>
    public sealed class PullCommand : AsyncCommand<PullSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, PullSettings settings, CancellationToken cancellationToken)
        {
            string destination = Path.GetFullPath(settings.Destination ?? ".");
            if (Directory.Exists(destination)) destination = Path.Combine(destination, settings.FileId.ToLowerInvariant());

            using FilesInterface client = new(settings.ServerAddress) { MaxConcurrency = Math.Max(1, settings.MaxConcurrency) };
            Stopwatch timer = Stopwatch.StartNew();
            DownloadResult result = await client.DownloadAsync(settings.FileId, destination, cancellationToken);
            timer.Stop();

            double seconds = Math.Max(timer.Elapsed.TotalSeconds, 0.001);
            Terminal.WriteLine($"Wrote {result.Size:N0} bytes to {destination}", ConsoleColor.Green);
            Terminal.WriteLine($"Downloaded {result.CompressedBytesDownloaded:N0} bytes, reused {result.BytesReused:N0} from the local copy. " +
                               $"Took {timer.Elapsed.TotalSeconds:N2} s, {result.CompressedBytesDownloaded / seconds / (1024 * 1024):N1} MB/s on the wire.", ConsoleColor.Gray);

            return 0;
        }
    }
}
