// Copyright 2026 Entex Interactive

using System.Buffers;
using Google.Protobuf;
using Grpc.Core;
using Vertex.Core.Net;
using Vertex.Server.Storage;

namespace Vertex.Server.FileServices
{
    public class FileTransferService : Files.FilesBase
    {
        private const int MaximumFindMissingIds = 10_000;
        private const int ExistenceCheckConcurrency = 16;
        private readonly ILogger<FileTransferService> _logger;
        private readonly IFileStorage _storage;

        public FileTransferService(ILogger<FileTransferService> logger, IFileStorage storage)
        {
            _logger = logger;
            _storage = storage;
        }

        public override async Task<UploadFileResponse> UploadAsync(IAsyncStreamReader<UploadFileRequest> requestStream, ServerCallContext context)
        {
            if (!await requestStream.MoveNext(context.CancellationToken))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Upload did not contain file metadata."));

            UploadFileRequest first = requestStream.Current;
            if (first.DataCase != UploadFileRequest.DataOneofCase.Metadata)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "File metadata must be the first upload message."));

            FileMetadata metadata = first.Metadata;
            if (metadata.FileSize < 0 || string.IsNullOrWhiteSpace(metadata.FileName))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "A file name and nonnegative file size are required."));

            string fileId = first.Metadata.FileId;
            if (!IsValidFileId(fileId)) throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid file id."));
            string fileName = Path.GetFileName(metadata.FileName);
            string contentType = string.IsNullOrWhiteSpace(metadata.ContentType) ? "application/octet-stream" : metadata.ContentType;
            _logger.LogInformation("Receiving {FileName} ({FileSize} bytes) as {FileId}", fileName, metadata.FileSize, fileId);

            try
            {
                await _storage.WriteAsync(fileId, contentType, metadata.FileSize,
                    ReadChunksAsync(requestStream, context.CancellationToken), context.CancellationToken);
            }
            catch (InvalidDataException exception)
            {
                throw new RpcException(new Status(StatusCode.DataLoss, exception.Message));
            }

            return new UploadFileResponse { FileId = fileId, FileName = fileName, BytesReceived = metadata.FileSize };
        }

        public override async Task DownloadAsync(DownloadFileRequest request, IServerStreamWriter<DownloadFileResponse> responseStream, ServerCallContext context)
        {
            if (!IsValidFileId(request.FileId)) throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid file id."));
            CancellationToken ct = context.CancellationToken;

            StoredFile stored;
            try
            {
                stored = await _storage.OpenReadAsync(request.FileId, ct);
            }
            catch (FileNotFoundException)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "File not found."));
            }

            await using (stored)
            {
                await responseStream.WriteAsync(new DownloadFileResponse
                {
                    Metadata = new FileMetadata { FileName = request.FileId, ContentType = "application/octet-stream", FileSize = stored.Length }
                }, ct);

                // Double-buffer: read the next chunk from storage while the previous one is sent to the client.
                // WriteAsync serializes the message before it completes, so a buffer can be reused (and
                // wrapped without copying) once its write has been awaited.
                byte[] current = ArrayPool<byte>.Shared.Rent(FileTransferLimits.ChunkSize);
                byte[] next = ArrayPool<byte>.Shared.Rent(FileTransferLimits.ChunkSize);
                try
                {
                    int currentLength = await FillAsync(stored.Content, current, ct);
                    while (currentLength > 0)
                    {
                        Task<int> readNext = FillAsync(stored.Content, next, ct);
                        try
                        {
                            await responseStream.WriteAsync(new DownloadFileResponse
                            {
                                Chunk = UnsafeByteOperations.UnsafeWrap(current.AsMemory(0, currentLength))
                            }, ct);
                        }
                        catch
                        {
                            // Never return a buffer to the pool while a read may still be writing into it.
                            try
                            {
                                await readNext;
                            }
                            catch
                            {
                                /* Preserve the original error. */
                            }

                            throw;
                        }

                        currentLength = await readNext;
                        (current, next) = (next, current);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(current);
                    ArrayPool<byte>.Shared.Return(next);
                }
            }
        }

        public override async Task<FindMissingResponse> FindMissing(FindMissingRequest request, ServerCallContext context)
        {
            if (request.FileIds.Count > MaximumFindMissingIds)
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"At most {MaximumFindMissingIds} file ids may be checked per request."));

            string[] ids = request.FileIds.Select(id => id.ToLowerInvariant()).Distinct().ToArray();
            if (ids.Any(id => !IsValidFileId(id))) throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid file id."));

            bool[] exists = new bool[ids.Length];
            await Parallel.ForAsync(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = ExistenceCheckConcurrency, CancellationToken = context.CancellationToken },
                async (i, ct) => exists[i] = await _storage.ExistsAsync(ids[i], ct));

            FindMissingResponse response = new();
            for (int i = 0; i < ids.Length; i++)
                if (!exists[i])
                    response.FileIds.Add(ids[i]);

            return response;
        }

        /// <summary>
        /// Reads until the buffer's chunk is full or the stream ends, so each gRPC message carries a full chunk
        /// even when the storage stream returns small reads.
        /// </summary>
        private static async Task<int> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            int total = 0;
            while (total < FileTransferLimits.ChunkSize)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, FileTransferLimits.ChunkSize - total), ct);
                if (read == 0) break;
                total += read;
            }

            return total;
        }

        private static bool IsValidFileId(string fileId)
        {
            return (fileId.Length == 32 && Guid.TryParseExact(fileId, "N", out _)) ||
                   (fileId.Length == 64 && fileId.All(Uri.IsHexDigit));
        }

        private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
            IAsyncStreamReader<UploadFileRequest> requestStream,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            while (await requestStream.MoveNext(cancellationToken))
            {
                UploadFileRequest request = requestStream.Current;
                if (request.DataCase != UploadFileRequest.DataOneofCase.Chunk) throw new InvalidDataException("Upload may contain metadata only as its first message.");
                if (request.Chunk.Length > FileTransferLimits.MaximumChunkSize) throw new InvalidDataException("Upload chunk exceeds the server chunk limit.");
                yield return request.Chunk.Memory;
            }
        }
    }
}
