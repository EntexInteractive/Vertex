// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Tests.Metrics;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// What a re-submit costs: unchanged assets should send nothing (only FindMissing lookups, which exercise the
    /// storage existence cache), and an asset with 5% rewritten should send only the chunks around the edit.
    /// </summary>
    public sealed class DedupScenario : IScenario
    {
        public string Name => "dedup";

        public string Description => "Re-upload of unchanged assets (should send nothing) and of 5%-edited copies (should send only changed chunks).";

        public async Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct)
        {
            IReadOnlyList<ChunkedFile> assets = await context.EnsureAssetsUploadedAsync(result, ct);
            using FilesInterface files = context.Files(context.Profile.MaximumConcurrency);

            Stopwatch clock = Stopwatch.StartNew();
            UploadResult again = await result.Timer("Re-upload unchanged assets").TimeAsync(() => files.UploadAsync(assets.ToArray(), ct: ct));
            result.Figure("Unchanged re-upload", $"{assets.Count} files ({Format.Bytes(assets.Sum(a => a.Size))}) checked in {Format.Duration(clock.Elapsed)}, " +
                                                 $"{Format.Bytes(again.CompressedBytesUploaded)} sent", clock.Elapsed.TotalMilliseconds, "ms");
            result.Check(again.FilesCommitted == 0 && again.ChunksUploaded == 0,
                $"Unchanged files were sent again: {again.FilesCommitted} files, {again.ChunksUploaded} chunks.");

            // The same chunks under a new file id: FindMissingChunks must find them all without re-sending.
            int chunkCount = assets.Sum(a => a.Chunks.Count);
            clock.Restart();
            HashSet<string> missing = await result.Timer($"FindMissingChunks ({chunkCount:N0} stored ids)")
                .TimeAsync(() => files.FindMissingChunksAsync(assets.SelectMany(a => a.Chunks).Select(c => c.Id), ct));
            result.Check(missing.Count == 0, $"{missing.Count} uploaded chunks are reported missing.");

            IReadOnlyList<(ChunkedFile Original, ChunkedFile Edited)> edited = await context.GetEditedAssetsAsync(result, ct);
            ChunkedFile[] copies = edited.Select(e => e.Edited).ToArray();
            long editedBytes = copies.Sum(c => c.Size);
            clock.Restart();
            UploadResult partial = await result.Timer($"Upload {copies.Length} assets with 5% edited").TimeAsync(() => files.UploadAsync(copies, ct: ct));
            TimeSpan elapsed = clock.Elapsed;
            context.MarkEditedUploaded();

            result.Figure("Edited re-upload", $"{Format.Bytes(partial.RawBytesUploaded)} of {Format.Bytes(editedBytes)} sent ({Format.Percent(partial.RawBytesUploaded, editedBytes)}), " +
                                              $"{partial.ChunksUploaded:N0} chunks new, {partial.ChunksDeduplicated:N0} reused, in {Format.Duration(elapsed)}",
                partial.RawBytesUploaded * 100.0 / Math.Max(1, editedBytes), "% sent");
            result.Check(partial.ChunksDeduplicated > 0, "Edited copies shared no chunks with their originals.");
            result.Check(partial.RawBytesUploaded < editedBytes / 4, $"A 5% edit sent {Format.Percent(partial.RawBytesUploaded, editedBytes)} of the data.");
        }
    }
}
