// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Tests.Metrics;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// Pulls the uploaded assets into an empty folder at each stream count (S3 reads and the server's send path),
    /// verifying every byte, then pulls edited versions over the old files to measure local chunk reuse.
    /// </summary>
    public sealed class DownloadScenario : IScenario
    {
        public string Name => "download";

        public string Description => "Large asset download throughput at each stream count, then an edited version over the old file (chunk reuse).";

        public async Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct)
        {
            IReadOnlyList<ChunkedFile> assets = await context.EnsureAssetsUploadedAsync(result, ct);
            long total = assets.Sum(a => a.Size);
            context.RequireFreeSpace(total, "Downloading the assets");

            foreach (int streams in context.Profile.TransferConcurrency)
            {
                string folder = context.NewDirectory($"download-{streams}");
                using FilesInterface files = context.Files(streams);
                LatencyRecorder perFile = result.Timer($"Download one {Format.Bytes(context.Profile.LargeFileSize)} asset, {Streams(streams)}");
                Stopwatch clock = Stopwatch.StartNew();
                long compressed = 0;
                string[] destinations = assets.Select(a => Path.Combine(folder, Path.GetFileName(a.Path))).ToArray();
                bool ok = true;
                for (int i = 0; i < assets.Count && ok; i++)
                {
                    int index = i;
                    (ok, DownloadResult? download) = await perFile.TryTimeAsync(() => files.DownloadAsync(assets[index].FileId, destinations[index], ct), ct);
                    compressed += download?.CompressedBytesDownloaded ?? 0;
                }

                TimeSpan elapsed = clock.Elapsed;
                if (!ok)
                {
                    result.Fail($"Download with {Streams(streams)} failed; see the errors above.");
                    continue;
                }

                result.Throughput($"Download, {Streams(streams)}", total, elapsed, $"{Format.Rate(compressed, elapsed)} on the wire");
                await VerifyAsync(result, assets, destinations, ct);
                Directory.Delete(folder, true);
            }

            // Pulling a new version of a file only fetches the chunks that changed.
            IReadOnlyList<(ChunkedFile Original, ChunkedFile Edited)> edited = await context.EnsureEditedUploadedAsync(result, ct);
            string updateFolder = context.NewDirectory("download-update");
            string[] targets = edited.Select(e => Path.Combine(updateFolder, Path.GetFileName(e.Original.Path))).ToArray();
            for (int i = 0; i < edited.Count; i++) File.Copy(edited[i].Original.Path, targets[i]);

            using FilesInterface updater = context.Files(context.Profile.MaximumConcurrency);
            LatencyRecorder update = result.Timer("Download edited version over the old file");
            long reused = 0, downloaded = 0, size = 0;
            Stopwatch updateClock = Stopwatch.StartNew();
            for (int i = 0; i < edited.Count; i++)
            {
                int index = i;
                DownloadResult download = await update.TimeAsync(() => updater.DownloadAsync(edited[index].Edited.FileId, targets[index], ct));
                reused += download.BytesReused;
                downloaded += download.RawBytesDownloaded;
                size += download.Size;
            }

            result.Figure("Update over old version", $"{Format.Percent(reused, size)} reused locally, {Format.Bytes(downloaded)} downloaded, in {Format.Duration(updateClock.Elapsed)}",
                reused * 100.0 / Math.Max(1, size), "% reused");
            result.Check(reused > size / 2, $"Only {Format.Percent(reused, size)} of a 5% edit was reused from the local file.");
            await VerifyAsync(result, edited.Select(e => e.Edited).ToArray(), targets, ct);
            Directory.Delete(updateFolder, true);
        }

        private static string Streams(int count)
        {
            return count == 1 ? "1 stream" : $"{count} streams";
        }

        private static async Task VerifyAsync(ScenarioResult result, IReadOnlyList<ChunkedFile> expected, string[] paths, CancellationToken ct)
        {
            ChunkedFile[] actual = await Task.Run(() => DataGenerator.Chunk(paths, ct), ct);
            int wrong = expected.Where((e, i) => e.FileId != actual[i].FileId).Count();
            result.Check(wrong == 0, $"{wrong} downloaded file(s) don't match what was uploaded.");
        }
    }
}
