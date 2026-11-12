// Copyright 2026 Entex Interactive

using System.Buffers;
using Grpc.Core;
using Vertex.Core.Net;
using Vertex.Core.Storage;
using Vertex.Server.Storage;

namespace Vertex.Server.FileServices
{
    /// <summary>
    /// gRPC endpoint for chunk-deduplicated file transfer. See Files.proto for the push/pull flow.
    /// </summary>
    public class FileTransferService : Files.FilesBase
    {
        /// <summary>Chunks one upload or download call may process at once (also bounded pod-wide).</summary>
        private const int ChunksPerCall = 8;

        /// <summary>Concurrent S3 existence checks per request.</summary>
        private const int LookupConcurrency = 32;

        private readonly ILogger<FileTransferService> _logger;
        private readonly IContentStore _store;
        private readonly TransferBudget _budget;

        public FileTransferService(ILogger<FileTransferService> logger, IContentStore store, TransferBudget budget)
        {
            _logger = logger;
            _store = store;
            _budget = budget;
        }

        public override async Task<FindMissingFilesResponse> FindMissingFiles(FindMissingFilesRequest request, ServerCallContext context)
        {
            if (request.FileIds.Count > FileTransferLimits.MaximumIdsPerRequest) throw TooMany("file ids");
            string[] ids = request.FileIds.Select(id => ContentId.IsValidFileId(id) ? id.ToLowerInvariant() : throw Invalid("Invalid file id.")).Distinct().ToArray();

            bool[] exists = new bool[ids.Length];
            await Parallel.ForAsync(0, ids.Length, Lookup(context), async (i, ct) => exists[i] = await _store.ManifestExistsAsync(ids[i], ct));

            FindMissingFilesResponse response = new();
            response.FileIds.AddRange(ids.Where((_, i) => !exists[i]));
            return response;
        }

        public override async Task<FindMissingChunksResponse> FindMissingChunks(FindMissingChunksRequest request, ServerCallContext context)
        {
            if (request.Ids.Count > FileTransferLimits.MaximumIdsPerRequest) throw TooMany("chunk ids");
            byte[][] ids = request.Ids.Select(ToChunkId).DistinctBy(id => ContentId.ToHex(id)).ToArray();

            bool[] exists = new bool[ids.Length];
            await Parallel.ForAsync(0, ids.Length, Lookup(context), async (i, ct) => exists[i] = await _store.GetChunkSizeAsync(ids[i], ct) is not null);

            FindMissingChunksResponse response = new();
            for (int i = 0; i < ids.Length; i++)
                if (!exists[i])
                    response.Ids.Add(Google.Protobuf.UnsafeByteOperations.UnsafeWrap(ids[i]));

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

            // The pod-wide budget bounds decompression buffers across all concurrent uploads.
            int stored = 0;
            ParallelOptions parallel = new() { MaxDegreeOfParallelism = ChunksPerCall, CancellationToken = context.CancellationToken };
            await Parallel.ForEachAsync(chunks, parallel, async (item, ct) =>
            {
                using IDisposable lease = await _budget.AcquireAsync(ct);
                await StoreChunkAsync(item.Id, item.Chunk, ct);
                Interlocked.Increment(ref stored);
            });

            long received = chunks.Sum(c => (long)c.Chunk.Data.Length);
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
            await Parallel.ForAsync(0, referenced.Length, Lookup(context), async (i, token) =>
            {
                long? stored = await _store.GetChunkSizeAsync(referenced[i].Id, token);
                if (stored != referenced[i].Size)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Chunk {ContentId.ToHex(referenced[i].Id)} is not stored; upload it before committing."));
            });

            await Parallel.ForAsync(0, fileIds.Length, Lookup(context), async (i, token) =>
            {
                if (!await _store.ManifestExistsAsync(fileIds[i], token))
                    await _store.PutManifestAsync(fileIds[i], request.Manifests[i], token);
            });

            _logger.LogInformation("Committed {Files} file(s) referencing {Chunks} distinct chunk(s)", fileIds.Length, referenced.Length);
            SubmitFilesResponse response = new();
            response.FileIds.AddRange(fileIds);
            return response;
        }

        public override async Task<FileManifest> GetManifest(GetManifestRequest request, ServerCallContext context)
        {
            if (!ContentId.IsValidFileId(request.FileId)) throw Invalid("Invalid file id.");
            return await _store.GetManifestAsync(request.FileId.ToLowerInvariant(), context.CancellationToken)
                   ?? throw new RpcException(new Status(StatusCode.NotFound, "File not found."));
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

        private static ParallelOptions Lookup(ServerCallContext context)
        {
            return new ParallelOptions { MaxDegreeOfParallelism = LookupConcurrency, CancellationToken = context.CancellationToken };
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
