// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Server;
using Vertex.Tests.Metrics;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// The first submit of a whole game project: tens of thousands of small files hashed, uploaded and submitted as
    /// one revision, then a full sync listing, an incremental edit and sync, and a parallel download of part of the
    /// tree the way a fresh workspace fills.
    /// </summary>
    public sealed class BulkSubmitScenario : IScenario
    {
        private const int EditedFiles = 100;
        private const int DownloadedFiles = 2_000;

        public string Name => "submit";

        public string Description => "First submit of a large tree of small files as one revision, then full and incremental sync.";

        public async Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct)
        {
            int count = context.Profile.SmallFiles;
            context.RequireFreeSpace(DataGenerator.EstimateTreeBytes(count) * 2, "The bulk submit tree");
            string root = context.NewDirectory("tree");
            string treePrefix = $"{context.PathPrefix}/tree";

            Stopwatch clock = Stopwatch.StartNew();
            IReadOnlyList<string> relative = await Task.Run(() => context.Data.WriteTree(root, count, ct), ct);
            result.Rate("Generate tree (local disk)", count, clock.Elapsed, "files");

            string[] fullPaths = relative.Select(r => Path.Combine(root, r)).ToArray();
            clock.Restart();
            ChunkedFile[] chunked = await Task.Run(() => DataGenerator.Chunk(fullPaths, ct), ct);
            long bytes = chunked.Sum(c => c.Size);
            result.Rate("Hash tree (client CPU)", count, clock.Elapsed, "files");

            using FilesInterface files = context.Files(context.Profile.MaximumConcurrency);
            clock.Restart();
            UploadResult upload = await result.Timer($"Upload {count:N0} files").TimeAsync(() => files.UploadAsync(chunked, ct: ct));
            result.Rate("Upload tree", upload.FilesCommitted, clock.Elapsed, "files");
            result.Throughput("Upload tree bytes", upload.RawBytesUploaded, clock.Elapsed, $"{upload.ChunksUploaded:N0} chunks");

            using RevisionInterface revisions = context.Revisions();
            Guid workspace = Guid.NewGuid();
            FileChange[] changes = relative.Select((path, i) => new FileChange { Path = $"{treePrefix}/{path}", FileId = chunked[i].FileId, BaseFileId = string.Empty }).ToArray();
            long revision = await result.Timer($"Submit {count:N0} files as one revision")
                .TimeAsync(() => revisions.SubmitAsync(context.ProjectId, context.BranchId, workspace, "bench", $"Bench {context.RunId}: initial tree", changes, ct));

            HeadChanges full = await result.Timer("Full sync listing (GetHeadChanges after #0, all pages)")
                .TimeAsync(() => revisions.GetHeadChangesAsync(context.ProjectId, context.BranchId, 0, ct));
            int ours = full.Files.Count(f => f.Path.StartsWith(treePrefix + "/", StringComparison.Ordinal));
            result.Figure("Full listing", $"{full.Files.Count:N0} files over {(full.Files.Count + 9_999) / 10_000} page(s), head #{full.HeadRevision}");
            result.Check(ours == count, $"Full listing has {ours:N0} of the {count:N0} submitted files.");

            // An everyday follow-up: edit a few files, submit them against their bases, and sync just that.
            int edits = Math.Min(EditedFiles, count);
            ulong stream = context.Data.NextStream();
            for (int i = 0; i < edits; i++)
                context.Data.WriteFile(fullPaths[i], new FileInfo(fullPaths[i]).Length + 16, stream + (ulong)i, true);
            ChunkedFile[] editedChunks = await Task.Run(() => DataGenerator.Chunk(fullPaths.Take(edits).ToArray(), ct), ct);
            await result.Timer($"Upload {edits} edited files").TimeAsync(() => files.UploadAsync(editedChunks, ct: ct));
            FileChange[] editChanges = editedChunks.Select((c, i) => new FileChange { Path = changes[i].Path, FileId = c.FileId, BaseFileId = changes[i].FileId }).ToArray();
            await result.Timer($"Submit {edits} edited files").TimeAsync(() =>
                revisions.SubmitAsync(context.ProjectId, context.BranchId, workspace, "bench", $"Bench {context.RunId}: edit", editChanges, ct));

            HeadChanges incremental = await result.Timer($"Incremental sync listing (after #{revision})")
                .TimeAsync(() => revisions.GetHeadChangesAsync(context.ProjectId, context.BranchId, revision, ct));
            int changedOurs = incremental.Files.Count(f => f.Path.StartsWith(treePrefix + "/", StringComparison.Ordinal));
            result.Check(changedOurs == edits, $"Incremental sync returned {changedOurs} of our files, expected {edits}.");

            // A fresh workspace pulling the tree: many small downloads in parallel.
            int sample = Math.Min(DownloadedFiles, count);
            string pull = context.NewDirectory("tree-download");
            LatencyRecorder perFile = result.Timer("Download one small file");
            using FilesInterface downloader = context.Files(1);
            clock.Restart();
            long pulled = 0;
            await Parallel.ForEachAsync(Enumerable.Range(edits, Math.Min(sample, count - edits)), new ParallelOptions { MaxDegreeOfParallelism = context.Profile.Clients * 2, CancellationToken = ct },
                async (i, token) =>
                {
                    (bool ok, DownloadResult? download) = await perFile.TryTimeAsync(() => downloader.DownloadAsync(chunked[i].FileId, Path.Combine(pull, relative[i]), token), token);
                    if (ok) Interlocked.Add(ref pulled, download!.Size);
                });

            result.Rate($"Download small files ({context.Profile.Clients * 2} in parallel)", perFile.Count, clock.Elapsed, "files");
            result.Check(perFile.ErrorCount == 0, $"{perFile.ErrorCount} small file downloads failed.");
            result.Note($"Tree: {count:N0} files, {Format.Bytes(bytes)}; revisions #{revision} and #{revision + 1} under {treePrefix}/.");
            Directory.Delete(pull, true);
        }
    }
}
