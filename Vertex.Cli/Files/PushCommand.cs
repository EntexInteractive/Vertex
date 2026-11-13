// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using EntexInteractive.Extensions;
using Vertex.Cli.Net;
using Vertex.Core.Client.IO;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;

namespace Vertex.Cli.Files
{
    public sealed class PushSettings : NetworkSettings
    {
        [CommandArgument(0, "[paths]")]
        [Description("Files or folders to upload. Defaults to the current folder.")]
        public string[] Paths { get; set; } = [];

        [CommandOption("--parallel")]
        [Description("The number of parallel operations to perform.")]
        public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
    }

    /// <summary>
    /// Test command: uploads files straight to the server's content store and prints their file ids, without a
    /// workspace or index. Use <c>vx pull &lt;file-id&gt;</c> to download one again. <c>vx submit</c> replaces this.
    /// </summary>
    public sealed class PushCommand : AsyncCommand<PushSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, PushSettings settings, CancellationToken cancellationToken)
        {
            string[] inputs = settings.Paths.Length > 0 ? settings.Paths : ["."];
            List<string> files = new();
            foreach (string input in inputs)
            {
                string full = Path.GetFullPath(input);
                if (File.Exists(full))
                {
                    files.Add(full);
                }
                else if (Directory.Exists(full))
                {
                    files.AddRange(FileSystemUtility.EnumerateFiles(full, IgnoreRules.Empty)
                        .Select(f => Path.Combine(full, f.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
                }
                else
                {
                    Terminal.WriteLine($"'{input}' does not exist.", ConsoleColor.Red);
                    return 1;
                }
            }

            if (files.Count == 0)
            {
                Terminal.WriteLine("Nothing to push.", ConsoleColor.Gray);
                return 0;
            }

            int parallel = Math.Max(1, settings.MaxConcurrency);
            Stopwatch timer = Stopwatch.StartNew();
            ChunkedFile[] chunked = files.Distinct(StringComparer.OrdinalIgnoreCase).AsParallel().WithDegreeOfParallelism(parallel).WithCancellation(cancellationToken)
                .Select(path => ContentChunker.ChunkFile(path, cancellationToken))
                .ToArray();

            TimeSpan hashing = timer.Elapsed;

            string baseDirectory = Directory.GetCurrentDirectory();
            using FilesInterface client = new(settings.ServerAddress) { MaxConcurrency = parallel };
            HashSet<ChunkedFile> stored = new();
            UploadResult result = await client.UploadAsync(chunked, file =>
            {
                lock (stored) stored.Add(file);
                Terminal.WriteLine($"{file.FileId}  {Path.GetRelativePath(baseDirectory, file.Path)} ({file.Size:N0} bytes)", ConsoleColor.Green);
            }, cancellationToken);

            timer.Stop();

            // Files the server already had aren't reported by the callback, so list them too: their ids are just as
            // useful for pulling.
            foreach (ChunkedFile file in chunked)
            {
                if (!stored.Contains(file))
                    Terminal.WriteLine($"{file.FileId}  {Path.GetRelativePath(baseDirectory, file.Path)} (already stored)", ConsoleColor.Gray);
            }

            long totalBytes = chunked.Sum(c => c.Size);
            double seconds = Math.Max(timer.Elapsed.TotalSeconds, 0.001);
            Terminal.WriteLine($"{result.FilesCommitted:N0} file(s) stored, {result.FilesAlreadyStored:N0} already on the server.", ConsoleColor.Gray);
            Terminal.WriteLine($"Chunks: {result.ChunksUploaded:N0} uploaded, {result.ChunksDeduplicated:N0} deduplicated. " +
                               $"Sent {result.CompressedBytesUploaded:N0} bytes for {totalBytes:N0} bytes of content.", ConsoleColor.Gray);

            Terminal.WriteLine($"Took {timer.Elapsed.TotalSeconds:N2} s ({hashing.TotalSeconds:N2} s hashing), " +
                               $"{result.CompressedBytesUploaded / seconds / (1024 * 1024):N1} MB/s on the wire.", ConsoleColor.Gray);
            return 0;
        }
    }
}
