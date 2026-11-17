// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Tests.Metrics;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// Pushes new large assets at each stream count in the profile. Each level gets its own assets, so nothing
    /// deduplicates and every byte goes to S3. Half the assets are incompressible (textures, audio), half compress
    /// about 2:1.
    /// </summary>
    public sealed class UploadScenario : IScenario
    {
        public string Name => "upload";

        public string Description => "Large asset upload throughput at each stream count (new data, nothing deduplicated).";

        public async Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct)
        {
            IReadOnlyList<ChunkedFile> assets = await context.GetAssetsAsync(result, ct);
            int[] levels = context.Profile.TransferConcurrency.Take(assets.Count).ToArray();
            if (levels.Length < context.Profile.TransferConcurrency.Length)
                result.Note($"Only {assets.Count} assets, so only {levels.Length} stream counts were tested. Raise --large-files to test more.");

            for (int level = 0; level < levels.Length; level++)
            {
                int streams = levels[level];
                ChunkedFile[] group = assets.Where((_, i) => i % levels.Length == level).ToArray();
                using FilesInterface files = context.Files(streams);
                LatencyRecorder timer = result.Timer($"Upload {group.Length} assets, {Streams(streams)}");
                Stopwatch clock = Stopwatch.StartNew();
                try
                {
                    UploadResult upload = await timer.TimeAsync(() => files.UploadAsync(group, ct: ct));
                    TimeSpan elapsed = clock.Elapsed;
                    result.Throughput($"Upload, {Streams(streams)}", upload.RawBytesUploaded, elapsed,
                        $"{Format.Rate(upload.CompressedBytesUploaded, elapsed)} on the wire, {upload.ChunksUploaded:N0} chunks");
                    result.Check(upload.ChunksDeduplicated == 0 && upload.FilesAlreadyStored == 0,
                        $"{upload.ChunksDeduplicated} chunks of new random data were already on the server.");
                    result.Check(upload.FilesCommitted == group.Length, $"Committed {upload.FilesCommitted} of {group.Length} files.");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    result.Fail($"Upload with {Streams(streams)} failed: {LatencyRecorder.Classify(ex)}: {ex.Message}");
                }
            }

            if (result.Failures.Count == 0) context.MarkAssetsUploaded();
        }

        private static string Streams(int count)
        {
            return count == 1 ? "1 stream" : $"{count} streams";
        }
    }
}
