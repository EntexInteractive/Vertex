// Copyright 2026 Entex Interactive

using System.Buffers;
using Google.Protobuf;
using Grpc.Core;
using Vertex.Core.Net;
using Vertex.Core.Storage;
using Vertex.Server.Database;
using Vertex.Server.Storage;
using Vertex.Server.Telemetry;

namespace Vertex.Server.FileServices
{
    /// <summary>
    /// gRPC endpoint for chunk-deduplicated file transfer. See Files.proto for the push/pull flow.
    /// </summary>
    public class FileTransferService : Files.FilesBase
    {
        /// <summary>Chunks one download call, or an oversized upload call, may process at once (also bounded pod-wide).</summary>
        private const int ChunksPerCall = 8;

        /// <summary>
        /// Chunks one normal upload call stores at once. A normal call carries at most one maximum-size chunk of raw data
        /// (see <see cref="FileTransferLimits.MaximumUploadRequestBytes"/>), so it fits one budget slot however many small
        /// chunks it packs, and they can all go to S3 together.
        /// </summary>
        private const int SmallChunksPerCall = 32;

        /// <summary>
        /// How long a manifest stays cached. Manifests are content-addressed and never change, so this only bounds memory
        /// and Redis use for files nobody downloads any more.
        /// </summary>
        private static readonly TimeSpan ManifestLifetime = TimeSpan.FromHours(6);

        private readonly ILogger<FileTransferService> _logger;
        private readonly IContentStore _store;
        private readonly FilesRepository _files;
        private readonly TransferBudget _budget;
        private readonly CacheContext _cache;

        public FileTransferService(ILogger<FileTransferService> logger, IContentStore store, FilesRepository files, TransferBudget budget, CacheContext cache)
        {
            _logger = logger;
            _store = store;
            _files = files;
            _budget = budget;
            _cache = cache;
        }

        public override async Task<FindMissingFilesResponse> FindMissingFiles(FindMissingFilesRequest request, ServerCallContext context)
        {
            if (request.FileIds.Count > FileTransferLimits.MaximumIdsPerRequest) throw TooMany("file ids");
            string[] ids = request.FileIds.Select(id => ContentId.IsValidFileId(id) ? id.ToLowerInvariant() : throw Invalid("Invalid file id.")).Distinct().ToArray();
            HashSet<string> stored = await _files.GetStoredManifestsAsync(ids, context.CancellationToken);

            FindMissingFilesResponse response = new();
            response.FileIds.AddRange(ids.Where(id => !stored.Contains(id)));
            VertexMetrics.Files.Add(response.FileIds.Count, VertexMetrics.Tag("result", "new"));
            VertexMetrics.Files.Add(ids.Length - response.FileIds.Count, VertexMetrics.Tag("result", "deduplicated"));
            return response;
        }

        public override async Task<FindMissingChunksResponse> FindMissingChunks(FindMissingChunksRequest request, ServerCallContext context)
        {
            if (request.Ids.Count > FileTransferLimits.MaximumIdsPerRequest) throw TooMany("chunk ids");
            byte[][] ids = request.Ids.Select(ToChunkId).DistinctBy(id => ContentId.ToHex(id)).ToArray();
            Dictionary<string, long> stored = await _files.GetChunkSizesAsync(ids, context.CancellationToken);

            FindMissingChunksResponse response = new();
            foreach (byte[] id in ids)
                if (!stored.ContainsKey(ContentId.ToHex(id)))
                    response.Ids.Add(Google.Protobuf.UnsafeByteOperations.UnsafeWrap(id));

            VertexMetrics.Chunks.Add(response.Ids.Count, VertexMetrics.Tag("result", "new"));
            VertexMetrics.Chunks.Add(ids.Length - response.Ids.Count, VertexMetrics.Tag("result", "deduplicated"));

            return response;
        }

        public override async Task<UploadChunksResponse> UploadChunks(UploadChunksRequest request, ServerCallContext context)
        {
            // Validate everything before storing anything, so a bad request stores nothing.
            (byte[] Id, ChunkData Chunk)[] chunks = request.Chunks.Select(chunk =>
            {
                byte[] id = ToChunkId(chunk.Id);
                if (chunk.Size is < 1 or > ContentChunker.MaximumChunkSize) throw Invalid("Chunk size is out of range.");
                if (chunk.Data.IsEmpty || chunk.Data.Length > ChunkCodec.MaximumCompressedSize) throw Invalid("Chunk data is out of range.");
                return (id, chunk);
            }).ToArray();

            // The pod-wide budget bounds decompression buffers across all concurrent uploads. A normal call holds at most
            // one maximum-size chunk of raw data, so one slot covers all of it and its chunks go to S3 together; an
            // oversized call takes a slot per chunk.
            int stored = 0;
            long raw = chunks.Sum(c => c.Chunk.Size);
            bool small = raw <= FileTransferLimits.MaximumUploadRequestBytes;
            using (IDisposable? callLease = small ? await _budget.AcquireAsync(context.CancellationToken) : null)
            {
                ParallelOptions parallel = new() { MaxDegreeOfParallelism = small ? SmallChunksPerCall : ChunksPerCall, CancellationToken = context.CancellationToken };
                await Parallel.ForEachAsync(chunks, parallel, async (item, ct) =>
                {
                    using IDisposable? lease = small ? null : await _budget.AcquireAsync(ct);
                    await StoreChunkAsync(item.Id, item.Chunk, ct);
                    Interlocked.Increment(ref stored);
                });
            }

            // Indexed only once every chunk of the call is in S3, all in one commit.
            await _files.AddChunksAsync(chunks.Select(c => (c.Id, c.Chunk.Size)).ToArray(), context.CancellationToken);

            long received = chunks.Sum(c => (long)c.Chunk.Data.Length);
            VertexMetrics.BytesReceived.Add(received);
            _logger.LogDebug("Stored {Chunks} chunks ({Bytes} compressed bytes)", stored, received);
            return new UploadChunksResponse { ChunksStored = stored, BytesReceived = received };
        }

        public override async Task<SubmitFilesResponse> SubmitFiles(SubmitFilesRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            if (request.Manifests.Sum(m => m.Chunks.Count) > FileTransferLimits.MaximumChunksPerCommit) throw TooMany("chunks");

            // Validate every manifest and compute its id server-side; clients never assign ids.
            string[] fileIds = new string[request.Manifests.Count];
            Dictionary<string, (byte[] Id, long Size)> chunks = new(StringComparer.Ordinal);
            for (int i = 0; i < request.Manifests.Count; i++)
            {
                FileManifest manifest = request.Manifests[i];
                long total = 0;
                foreach (ChunkRef chunk in manifest.Chunks)
                {
                    if (chunk.Size is < 1 or > ContentChunker.MaximumChunkSize) throw Invalid("Manifest contains an invalid chunk size.");
                    byte[] id = ToChunkId(chunk.Id);
                    if (chunks.TryGetValue(ContentId.ToHex(id), out (byte[] Id, long Size) seen) && seen.Size != chunk.Size)
                        throw Invalid("Manifest lists one chunk with two sizes.");

                    chunks[ContentId.ToHex(id)] = (id, chunk.Size);
                    total += chunk.Size;
                }

                if (total != manifest.Size) throw Invalid("Manifest chunk sizes do not add up to the file size.");
                fileIds[i] = ContentId.ComputeFileId(manifest);
            }

            // Every referenced chunk must be stored with the declared size before a manifest can point at it.
            (byte[] Id, long Size)[] referenced = chunks.Values.ToArray();
            Dictionary<string, long> storedSizes = await _files.GetChunkSizesAsync(referenced.Select(c => c.Id).ToArray(), ct);
            foreach ((byte[] id, long size) in referenced)
            {
                if (!storedSizes.TryGetValue(ContentId.ToHex(id), out long stored) || stored != size)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Chunk {ContentId.ToHex(id)} is not stored; upload it before committing."));
            }

            HashSet<string> known = await _files.GetStoredManifestsAsync(fileIds, ct);
            (string FileId, FileManifest Manifest)[] added = fileIds.Select((id, i) => (FileId: id, Manifest: request.Manifests[i]))
                .Where(m => !known.Contains(m.FileId)).DistinctBy(m => m.FileId).ToArray();
            await _files.AddManifestsAsync(added, ct);

            _logger.LogInformation("Committed {Files} file(s) referencing {Chunks} distinct chunk(s)", fileIds.Length, referenced.Length);
            SubmitFilesResponse response = new();
            response.FileIds.AddRange(fileIds);
            return response;
        }

        public override async Task<FileManifest> GetManifest(GetManifestRequest request, ServerCallContext context)
        {
            if (!ContentId.IsValidFileId(request.FileId)) throw Invalid("Invalid file id.");
            string fileId = request.FileId.ToLowerInvariant();

            // Every client that syncs a file asks for the same manifest, and it never changes, so cache it. Only found
            // manifests are cached: a file asked for before its upload finished must be found once it has. Memory only:
            // a miss is one primary-key query, so Redis stays out of the download path.
            byte[]? data = await _cache.GetOrSetIfFoundAsync(CacheKeys.Manifest(fileId), async ct =>
            {
                // Manifests written before the content index existed are only in S3.
                FileManifest? manifest = await _files.GetManifestAsync(fileId, ct) ?? await _store.GetManifestAsync(fileId, ct);
                return manifest?.ToByteArray();
            }, ManifestLifetime, context.CancellationToken, CacheContext.MemoryOnly);

            return data is not null ? FileManifest.Parser.ParseFrom(data) : throw new RpcException(new Status(StatusCode.NotFound, "File not found."));
        }

        public override async Task DownloadChunks(DownloadChunksRequest request, IServerStreamWriter<ChunkData> responseStream, ServerCallContext context)
        {
            if (request.Ids.Count > FileTransferLimits.MaximumChunksPerDownload) throw TooMany("chunk ids");
            byte[][] ids = request.Ids.Select(ToChunkId).DistinctBy(id => ContentId.ToHex(id)).ToArray();

            // Fetch several chunks from S3 at once; writes to the response stream must be serialized.
            using SemaphoreSlim writeLock = new(1);
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = ChunksPerCall, CancellationToken = context.CancellationToken }, async (id, ct) =>
            {
                using IDisposable lease = await _budget.AcquireAsync(ct);
                StoredChunk chunk;
                try
                {
                    chunk = await _store.GetChunkAsync(id, ct);
                }
                catch (FileNotFoundException)
                {
                    throw new RpcException(new Status(StatusCode.NotFound, $"Chunk {ContentId.ToHex(id)} not found."));
                }

                await writeLock.WaitAsync(ct);
                try
                {
                    await responseStream.WriteAsync(new ChunkData
                    {
                        Id = Google.Protobuf.UnsafeByteOperations.UnsafeWrap(id),
                        Size = chunk.Size,
                        Data = Google.Protobuf.UnsafeByteOperations.UnsafeWrap(chunk.CompressedData)
                    }, ct);

                    VertexMetrics.BytesSent.Add(chunk.CompressedData.Length);
                }
                finally
                {
                    writeLock.Release();
                }
            });
        }

        /// <summary>
        /// Verifies a chunk's content against its id (so no client can store bytes under someone else's hash),
        /// then writes it to storage. Releases the call and budget slots when done.
        /// </summary>
        private async Task StoreChunkAsync(byte[] id, ChunkData chunk, CancellationToken ct)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)chunk.Size);
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        ChunkCodec.DecompressAndVerify(id, chunk.Data.Span, buffer.AsSpan(0, (int)chunk.Size));
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new RpcException(new Status(StatusCode.DataLoss, $"Chunk {ContentId.ToHex(id)}: {exception.Message}"));
                    }
                }, ct);

                await _store.PutChunkAsync(id, chunk.Size, chunk.Data.Memory, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static byte[] ToChunkId(Google.Protobuf.ByteString id)
        {
            return id.Length == ContentId.HashSize ? id.ToByteArray() : throw Invalid("Chunk id must be a SHA-256 hash.");
        }

        private static RpcException Invalid(string message)
        {
            return new RpcException(new Status(StatusCode.InvalidArgument, message));
        }

        private static RpcException TooMany(string what)
        {
            return new RpcException(new Status(StatusCode.InvalidArgument, $"Too many {what} in one request."));
        }
    }
}
