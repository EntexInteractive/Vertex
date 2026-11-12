// Copyright 2026 Entex Interactive

using System.Buffers;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Win32.SafeHandles;
using Vertex.Core.Storage;
using Vertex.Server;

namespace Vertex.Core.Net.Interfaces
{
    /// <summary>
    /// Outcome of a push: how much was actually sent versus deduplicated.
    /// </summary>
    public sealed record PushResult(int FilesCommitted, int FilesAlreadyStored, int ChunksUploaded, int ChunksDeduplicated, long RawBytesUploaded, long CompressedBytesUploaded);

    /// <summary>
    /// Outcome of a pull: how much was downloaded versus reused from the existing local file.
    /// </summary>
    public sealed record PullResult(long Size, long RawBytesDownloaded, long CompressedBytesDownloaded, long BytesReused);

    public class FilesInterface : BaseInterface
    {
        /// <summary>
        /// Parallel upload/download streams per operation.
        /// </summary>
        public int MaxConcurrency { get; set; } = 4;

        public Files.FilesClient FilesClient { get; }

        public FilesInterface(string url) : base(url)
        {
            FilesClient = new Files.FilesClient(GrpcChannel);
        }

        /// <summary>
        /// Returns the file ids (lowercase hex) the server does not already store.
        /// </summary>
        public async Task<HashSet<string>> FindMissingFilesAsync(IEnumerable<string> fileIds, CancellationToken ct = default)
        {
            HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
            foreach (string[] batch in fileIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(FileTransferLimits.MaximumIdsPerRequest))
            {
                FindMissingFilesRequest request = new();
                request.FileIds.AddRange(batch);
                FindMissingFilesResponse response = await FilesClient.FindMissingFilesAsync(request, cancellationToken: ct);
                missing.UnionWith(response.FileIds);
            }

            return missing;
        }

        /// <summary>
        /// Returns the chunk ids (lowercase hex) the server does not already store.
        /// </summary>
        public async Task<HashSet<string>> FindMissingChunksAsync(IEnumerable<byte[]> chunkIds, CancellationToken ct = default)
        {
            HashSet<string> missing = new(StringComparer.Ordinal);
            foreach (byte[][] batch in chunkIds.Chunk(FileTransferLimits.MaximumIdsPerRequest))
            {
                FindMissingChunksRequest request = new();
                request.Ids.AddRange(batch.Select(id => UnsafeByteOperations.UnsafeWrap(id)));
                FindMissingChunksResponse response = await FilesClient.FindMissingChunksAsync(request, cancellationToken: ct);
                missing.UnionWith(response.Ids.Select(id => ContentId.ToHex(id.Span)));
            }

            return missing;
        }

        /// <summary>
        /// Pushes chunked files: skips files and chunks the server already stores, uploads the rest over parallel
        /// streams, then commits each file's manifest.
        /// </summary>
        /// <param name="files">Files chunked with <see cref="ContentChunker.ChunkFile"/>.</param>
        /// <param name="onCommitted">Invoked for each newly stored file.</param>
        /// <param name="ct">Cancels the push.</param>
        public async Task<PushResult> PushAsync(IReadOnlyCollection<ChunkedFile> files, Action<ChunkedFile>? onCommitted = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(files);
            List<ChunkedFile> unique = files.DistinctBy(f => f.FileId).ToList();
            HashSet<string> missingFiles = await FindMissingFilesAsync(unique.Select(f => f.FileId), ct);
            List<ChunkedFile> pending = unique.Where(f => missingFiles.Contains(f.FileId)).ToList();

            // One source location per distinct chunk; identical chunks across and within files upload once.
            Dictionary<string, (ChunkedFile File, FileChunk Chunk)> sources = new(StringComparer.Ordinal);
            foreach (ChunkedFile file in pending)
            foreach (FileChunk chunk in file.Chunks)
                sources.TryAdd(ContentId.ToHex(chunk.Id), (file, chunk));

            HashSet<string> missingChunks = await FindMissingChunksAsync(sources.Values.Select(s => s.Chunk.Id), ct);
            List<(ChunkedFile File, FileChunk Chunk)> toUpload = sources.Where(s => missingChunks.Contains(s.Key)).Select(s => s.Value).ToList();

            long rawBytes = 0, compressedBytes = 0;
            await Parallel.ForEachAsync(BatchUploads(toUpload), new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct }, async (batch, token) =>
            {
                (long raw, long compressed) = await UploadBatchAsync(batch, token);
                Interlocked.Add(ref rawBytes, raw);
                Interlocked.Add(ref compressedBytes, compressed);
            });

            foreach (List<ChunkedFile> batch in BatchCommits(pending))
            {
                SubmitFilesRequest request = new();
                request.Manifests.AddRange(batch.Select(f => f.ToManifest()));
                SubmitFilesResponse response = await FilesClient.SubmitFilesAsync(request, cancellationToken: ct);
                for (int i = 0; i < batch.Count; i++)
                {
                    if (!string.Equals(response.FileIds[i], batch[i].FileId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Server computed a different id for {batch[i].Path}; client and server chunk formats differ.");

                    onCommitted?.Invoke(batch[i]);
                }
            }

            return new PushResult(pending.Count, unique.Count - pending.Count, toUpload.Count, sources.Count - toUpload.Count, rawBytes, compressedBytes);
        }

        /// <summary>
        /// Downloads a file by id. Chunks that already exist in the current file at <paramref name="destinationPath"/>
        /// are reused, so pulling a new version of a file only transfers the changed chunks. The file is published
        /// only after every chunk has been verified.
        /// </summary>
        public async Task<PullResult> DownloadAsync(string fileId, string destinationPath, CancellationToken ct = default)
        {
            if (!ContentId.IsValidFileId(fileId)) throw new ArgumentException("File id must be a 64-character hex string.", nameof(fileId));
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
            fileId = fileId.ToLowerInvariant();
            string destination = Path.GetFullPath(destinationPath);
            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            FileManifest manifest = await FilesClient.GetManifestAsync(new GetManifestRequest { FileId = fileId }, cancellationToken: ct);
            Dictionary<string, PendingChunk> needed = PlanChunks(fileId, manifest);

            string temporaryPath = destination + ".vxtmp";
            long reused = 0, rawDownloaded = 0, compressedDownloaded = 0;
            try
            {
                using (SafeFileHandle output = File.OpenHandle(temporaryPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous))
                {
                    RandomAccess.SetLength(output, manifest.Size);
                    if (File.Exists(destination) && needed.Count > 0)
                        reused = await ReuseLocalChunksAsync(destination, needed, output, ct);

                    await Parallel.ForEachAsync(BatchDownloads(needed.Values), new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct }, async (batch, token) =>
                    {
                        (long raw, long compressed) = await DownloadBatchAsync(batch, output, token);
                        Interlocked.Add(ref rawDownloaded, raw);
                        Interlocked.Add(ref compressedDownloaded, compressed);
                    });
                }

                File.Move(temporaryPath, destination, true);
                return new PullResult(manifest.Size, rawDownloaded, compressedDownloaded, reused);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private sealed class PendingChunk(byte[] id, int size)
        {
            public byte[] Id { get; } = id;
            public int Size { get; } = size;
            public List<long> Offsets { get; } = new();
        }

        /// <summary>
        /// Verifies the manifest against the requested id and maps each distinct chunk to every offset it fills.
        /// </summary>
        private static Dictionary<string, PendingChunk> PlanChunks(string fileId, FileManifest manifest)
        {
            if (ContentId.ComputeFileId(manifest) != fileId) throw new InvalidDataException("Server returned a manifest that does not match the file id.");

            Dictionary<string, PendingChunk> chunks = new(StringComparer.Ordinal);
            long offset = 0;
            foreach (ChunkRef chunk in manifest.Chunks)
            {
                if (chunk.Size is < 1 or > ContentChunker.MaximumChunkSize) throw new InvalidDataException("Manifest contains an invalid chunk size.");
                string key = ContentId.ToHex(chunk.Id.Span);
                if (!chunks.TryGetValue(key, out PendingChunk? pending))
                    chunks.Add(key, pending = new PendingChunk(chunk.Id.ToByteArray(), (int)chunk.Size));
                else if (pending.Size != chunk.Size) throw new InvalidDataException("Manifest lists one chunk with two sizes.");

                pending.Offsets.Add(offset);
                offset += chunk.Size;
            }

            if (offset != manifest.Size) throw new InvalidDataException("Manifest chunk sizes do not add up to the file size.");
            return chunks;
        }

        /// <summary>
        /// Chunks the existing local file and copies any chunks the new version shares, removing them from
        /// <paramref name="needed"/>. Chunk ids are content hashes, so a matching id means matching bytes.
        /// </summary>
        private static async Task<long> ReuseLocalChunksAsync(string path, Dictionary<string, PendingChunk> needed, SafeFileHandle output, CancellationToken ct)
        {
            ChunkedFile local;
            try
            {
                local = await Task.Run(() => ContentChunker.ChunkFile(path, ct), ct);
            }
            catch (IOException)
            {
                return 0; // Unreadable local copy: download everything.
            }

            long reused = 0;
            using SafeFileHandle input = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ContentChunker.MaximumChunkSize);
            try
            {
                foreach (FileChunk chunk in local.Chunks)
                {
                    if (!needed.Remove(ContentId.ToHex(chunk.Id), out PendingChunk? pending)) continue;
                    Memory<byte> data = buffer.AsMemory(0, chunk.Length);
                    await ReadExactlyAsync(input, data, chunk.Offset, ct);
                    foreach (long offset in pending.Offsets)
                        await RandomAccess.WriteAsync(output, data, offset, ct);

                    reused += (long)chunk.Length * pending.Offsets.Count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return reused;
        }

        private async Task<(long Raw, long Compressed)> DownloadBatchAsync(PendingChunk[] batch, SafeFileHandle output, CancellationToken ct)
        {
            Dictionary<string, PendingChunk> expected = batch.ToDictionary(c => ContentId.ToHex(c.Id), StringComparer.Ordinal);
            DownloadChunksRequest request = new();
            request.Ids.AddRange(batch.Select(c => UnsafeByteOperations.UnsafeWrap(c.Id)));

            long raw = 0, compressed = 0;
            using AsyncServerStreamingCall<ChunkData> call = FilesClient.DownloadChunks(request, cancellationToken: ct);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ContentChunker.MaximumChunkSize);
            try
            {
                await foreach (ChunkData message in call.ResponseStream.ReadAllAsync(ct))
                {
                    if (!expected.Remove(ContentId.ToHex(message.Id.Span), out PendingChunk? chunk) || message.Size != chunk.Size)
                        throw new InvalidDataException("Server sent a chunk that was not requested.");

                    Memory<byte> data = buffer.AsMemory(0, chunk.Size);
                    ChunkCodec.DecompressAndVerify(chunk.Id, message.Data.Span, data.Span);
                    foreach (long offset in chunk.Offsets)
                        await RandomAccess.WriteAsync(output, data, offset, ct);

                    raw += (long)chunk.Size * chunk.Offsets.Count;
                    compressed += message.Data.Length;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (expected.Count > 0) throw new InvalidDataException($"Server did not return {expected.Count} requested chunk(s).");
            return (raw, compressed);
        }

        private async Task<(long Raw, long Compressed)> UploadBatchAsync(List<(ChunkedFile File, FileChunk Chunk)> batch, CancellationToken ct)
        {
            using AsyncClientStreamingCall<ChunkData, UploadChunksResponse> call = FilesClient.UploadChunks(cancellationToken: ct);
            long raw = 0;

            // Read + compress the next chunk on the thread pool while the current one is on the wire.
            Task<ChunkData> next = Task.Run(() => PrepareChunk(batch[0].File, batch[0].Chunk), ct);
            for (int i = 0; i < batch.Count; i++)
            {
                ChunkData message = await next;
                if (i + 1 < batch.Count)
                {
                    (ChunkedFile file, FileChunk chunk) = batch[i + 1];
                    next = Task.Run(() => PrepareChunk(file, chunk), ct);
                }

                await call.RequestStream.WriteAsync(message, ct);
                raw += message.Size;
            }

            await call.RequestStream.CompleteAsync();
            UploadChunksResponse response = await call.ResponseAsync;
            return (raw, response.BytesReceived);
        }

        private static ChunkData PrepareChunk(ChunkedFile file, FileChunk chunk)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
            try
            {
                Span<byte> data = buffer.AsSpan(0, chunk.Length);
                using (SafeFileHandle input = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int total = 0;
                    while (total < data.Length)
                    {
                        int read = RandomAccess.Read(input, data[total..], chunk.Offset + total);
                        if (read == 0) break;
                        total += read;
                    }

                    if (total != data.Length) throw new IOException($"{file.Path} changed while it was being pushed.");
                }

                if (!SHA256.HashData(data).AsSpan().SequenceEqual(chunk.Id))
                    throw new IOException($"{file.Path} changed while it was being pushed.");

                return new ChunkData
                {
                    Id = UnsafeByteOperations.UnsafeWrap(chunk.Id),
                    Size = chunk.Length,
                    Data = UnsafeByteOperations.UnsafeWrap(ChunkCodec.Compress(data))
                };
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task ReadExactlyAsync(SafeFileHandle handle, Memory<byte> buffer, long offset, CancellationToken ct)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await RandomAccess.ReadAsync(handle, buffer[total..], offset + total, ct);
                if (read == 0) throw new IOException("Local file changed while it was being read.");
                total += read;
            }
        }

        /// <summary>
        /// Groups chunks into upload streams capped at <see cref="FileTransferLimits.MaximumUploadStreamBytes"/>,
        /// keeping a file's chunks together so reads stay mostly sequential.
        /// </summary>
        private static IEnumerable<List<(ChunkedFile File, FileChunk Chunk)>> BatchUploads(List<(ChunkedFile File, FileChunk Chunk)> chunks)
        {
            List<(ChunkedFile, FileChunk)> batch = new();
            long bytes = 0;
            foreach ((ChunkedFile File, FileChunk Chunk) item in chunks)
            {
                if (batch.Count > 0 && bytes + item.Chunk.Length > FileTransferLimits.MaximumUploadStreamBytes)
                {
                    yield return batch;
                    batch = new List<(ChunkedFile, FileChunk)>();
                    bytes = 0;
                }

                batch.Add(item);
                bytes += item.Chunk.Length;
            }

            if (batch.Count > 0) yield return batch;
        }

        /// <summary>
        /// Groups chunks into download requests of bounded count and size so work spreads across streams.
        /// </summary>
        private static IEnumerable<PendingChunk[]> BatchDownloads(IEnumerable<PendingChunk> chunks)
        {
            List<PendingChunk> batch = new();
            long bytes = 0;
            foreach (PendingChunk chunk in chunks)
            {
                if (batch.Count > 0 && (batch.Count == FileTransferLimits.MaximumChunksPerDownload || bytes + chunk.Size > FileTransferLimits.MaximumUploadStreamBytes))
                {
                    yield return batch.ToArray();
                    batch.Clear();
                    bytes = 0;
                }

                batch.Add(chunk);
                bytes += chunk.Size;
            }

            if (batch.Count > 0) yield return batch.ToArray();
        }

        private static IEnumerable<List<ChunkedFile>> BatchCommits(List<ChunkedFile> files)
        {
            List<ChunkedFile> batch = new();
            int chunks = 0;
            foreach (ChunkedFile file in files)
            {
                if (file.Chunks.Count > FileTransferLimits.MaximumChunksPerCommit)
                    throw new InvalidOperationException($"{file.Path} has too many chunks to commit in one request.");

                if (batch.Count > 0 && chunks + file.Chunks.Count > FileTransferLimits.MaximumChunksPerCommit)
                {
                    yield return batch;
                    batch = new List<ChunkedFile>();
                    chunks = 0;
                }

                batch.Add(file);
                chunks += file.Chunks.Count;
            }

            if (batch.Count > 0) yield return batch;
        }
    }
}
