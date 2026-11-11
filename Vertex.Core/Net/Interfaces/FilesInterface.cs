// Copyright 2026 Entex Interactive

using System.Buffers;
using Google.Protobuf;
using Grpc.Core;
using Vertex.Core.Data;
using Vertex.Server;

namespace Vertex.Core.Net.Interfaces
{
    public class FilesInterface : BaseInterface
    {
        private const int FindMissingBatchSize = 1_000;

        public int ChunkSize { get; set; } = FileTransferLimits.ChunkSize;
        public Files.FilesClient FilesClient { get; }

        public FilesInterface(string url) : base(url)
        {
            FilesClient = new Files.FilesClient(GrpcChannel);
        }

        /// <summary>
        /// Returns the file ids the server does not already store, so callers can skip redundant uploads.
        /// </summary>
        public async Task<HashSet<string>> FindMissingAsync(IEnumerable<string> fileIds, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(fileIds);
            HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
            foreach (string[] batch in fileIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(FindMissingBatchSize))
            {
                FindMissingRequest request = new();
                request.FileIds.AddRange(batch);
                FindMissingResponse response = await FilesClient.FindMissingAsync(request, cancellationToken: ct);
                missing.UnionWith(response.FileIds);
            }

            return missing;
        }

        /// <summary>
        /// Uploads a file and returns the server's assigned file identifier and byte count.
        /// </summary>
        public async Task<UploadFileResponse> UploadAsync(VFile file, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(file);
            int chunkSize = GetChunkSize();
            using AsyncClientStreamingCall<UploadFileRequest, UploadFileResponse> call = FilesClient.UploadAsync(cancellationToken: ct);
            await call.RequestStream.WriteAsync(new UploadFileRequest
            {
                Metadata = new FileMetadata { FileName = file.Name, ContentType = "application/octet-stream", FileSize = file.PhysicalSize, FileId = file.CheckSum ?? throw new InvalidOperationException("File checksum must be generated before upload.") }
            }, ct);

            // bufferSize 0 disables FileStream's internal buffer; reads are already chunk-sized.
            await using FileStream input = new(file.Fullname, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);

            // Double-buffer: read the next chunk from disk while the previous one is on the wire.
            // WriteAsync serializes the message before completing, so the buffer can be wrapped
            // without copying and reused once its write has been awaited.
            byte[] current = ArrayPool<byte>.Shared.Rent(chunkSize);
            byte[] next = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                int currentLength = await FillAsync(input, current, chunkSize, ct);
                while (currentLength > 0)
                {
                    Task<int> readNext = FillAsync(input, next, chunkSize, ct);
                    try
                    {
                        await call.RequestStream.WriteAsync(new UploadFileRequest
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

            await call.RequestStream.CompleteAsync();
            return await call.ResponseAsync;
        }

        /// <summary>
        /// Uploads several files concurrently over the shared channel, skipping files the server already stores.
        /// Every file must already have a checksum.
        /// </summary>
        /// <param name="onUploaded">Invoked after each upload completes; may be called from multiple threads.</param>
        /// <returns>The number of files uploaded (excluding ones the server already had).</returns>
        public async Task<int> UploadManyAsync(IReadOnlyCollection<VFile> files, int maxConcurrency = 4, Action<VFile, UploadFileResponse>? onUploaded = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
            if (files.Any(f => string.IsNullOrEmpty(f.CheckSum))) throw new InvalidOperationException("File checksum must be generated before upload.");

            HashSet<string> missing = await FindMissingAsync(files.Select(f => f.CheckSum!), ct);

            // Content-addressed storage: identical files share an id, so upload each id only once.
            List<VFile> pending = files.Where(f => missing.Contains(f.CheckSum!)).DistinctBy(f => f.CheckSum!, StringComparer.OrdinalIgnoreCase).ToList();
            int uploaded = 0;
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct }, async (file, token) =>
            {
                UploadFileResponse response = await UploadAsync(file, token);
                Interlocked.Increment(ref uploaded);
                onUploaded?.Invoke(file, response);
            });

            return uploaded;
        }

        /// <summary>
        /// Downloads a file to a destination, publishing it only after the complete stream is received.
        /// </summary>
        public async Task<long> DownloadAsync(string fileId, string destinationPath, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
            string destination = Path.GetFullPath(destinationPath);
            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = destination + ".tmp";

            try
            {
                using AsyncServerStreamingCall<DownloadFileResponse> call = FilesClient.DownloadAsync(new DownloadFileRequest { FileId = fileId }, cancellationToken: ct);

                // bufferSize 0 disables FileStream's internal buffer; chunks are written directly.
                await using FileStream output = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
                FileMetadata? metadata = null;
                long bytesReceived = 0;
                Task pendingWrite = Task.CompletedTask;
                try
                {
                    while (await call.ResponseStream.MoveNext(ct))
                    {
                        DownloadFileResponse response = call.ResponseStream.Current;
                        if (response.DataCase == DownloadFileResponse.DataOneofCase.Metadata)
                        {
                            if (metadata is not null || bytesReceived != 0) throw new InvalidDataException("Invalid download stream metadata.");
                            metadata = response.Metadata;
                            if (metadata.FileSize < 0) throw new InvalidDataException("Server reported an invalid file size.");

                            // Reserve the full size up front to avoid repeated file growth and fragmentation.
                            output.SetLength(metadata.FileSize);
                        }
                        else if (response.DataCase == DownloadFileResponse.DataOneofCase.Chunk)
                        {
                            if (metadata is null || response.Chunk.Length > FileTransferLimits.MaximumChunkSize || bytesReceived > metadata.FileSize - response.Chunk.Length)
                                throw new InvalidDataException("Download stream exceeded its declared size or chunk limit.");

                            // Write this chunk to disk while the next one is received. Each message owns its
                            // own memory, so the chunk stays valid after the stream advances.
                            await pendingWrite;
                            pendingWrite = output.WriteAsync(response.Chunk.Memory, ct).AsTask();
                            bytesReceived += response.Chunk.Length;
                        }
                        else
                        {
                            throw new InvalidDataException("Download stream contained an empty message.");
                        }
                    }
                }
                catch
                {
                    // Ensure no write is in flight before the temporary file is deleted.
                    try
                    {
                        await pendingWrite;
                    }
                    catch
                    {
                        /* Preserve the original error. */
                    }

                    throw;
                }

                await pendingWrite;

                if (metadata is null || bytesReceived != metadata.FileSize)
                    throw new InvalidDataException("Download ended before the complete file arrived.");

                await output.FlushAsync(ct);
                await output.DisposeAsync();
                File.Move(temporaryPath, destination, true);
                return bytesReceived;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private int GetChunkSize()
        {
            if (ChunkSize < 1 || ChunkSize > FileTransferLimits.MaximumChunkSize)
                throw new InvalidOperationException($"ChunkSize must be between 1 and {FileTransferLimits.MaximumChunkSize} bytes.");

            return ChunkSize;
        }

        /// <summary>
        /// Reads until the chunk is full or the stream ends, so every message carries a full chunk.
        /// </summary>
        private static async Task<int> FillAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
                if (read == 0) break;
                total += read;
            }

            return total;
        }
    }
}
