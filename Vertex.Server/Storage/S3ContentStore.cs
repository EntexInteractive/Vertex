// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Server.Telemetry;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Amazon.S3;
using Amazon.S3.Model;
using Google.Protobuf;
using Vertex.Core.Storage;

namespace Vertex.Server.Storage
{
    /// <summary>
    /// A compressed chunk as stored, with its raw (uncompressed) size.
    /// </summary>
    public sealed record StoredChunk(byte[] CompressedData, long Size);

    /// <summary>
    /// Content-addressed storage for chunk bytes. Everything stored is immutable and keyed by its own hash, so writes
    /// are idempotent and safe to repeat or race. Which chunks and manifests exist is answered by the content index
    /// (<see cref="FileServices.FilesRepository"/>), not by asking this store.
    /// </summary>
    public interface IContentStore
    {
        Task PutChunkAsync(byte[] id, long size, ReadOnlyMemory<byte> compressedData, CancellationToken cancellationToken);

        /// <exception cref="FileNotFoundException">The chunk is not stored.</exception>
        Task<StoredChunk> GetChunkAsync(byte[] id, CancellationToken cancellationToken);

        /// <summary>
        /// A manifest written before the content index existed, or null. New manifests are only in the index.
        /// </summary>
        Task<FileManifest?> GetManifestAsync(string fileId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Stores each chunk as its own S3 object under <c>chunks/</c>, sharded by hash prefix to spread S3 request load
    /// across key partitions. Older manifests are read from <c>manifests/</c>.
    /// </summary>
    public sealed class S3ContentStore : IContentStore
    {
        private const string RawSizeMetadata = "raw-size";
        private readonly IAmazonS3 _s3;
        private readonly string _bucket;

        public S3ContentStore(IAmazonS3 s3, ServerSettings settings)
        {
            _s3 = s3;
            _bucket = settings.Storage.Bucket;
        }

        public async Task PutChunkAsync(byte[] id, long size, ReadOnlyMemory<byte> compressedData, CancellationToken cancellationToken)
        {
            string key = ChunkKey(id);
            PutObjectRequest request = new()
            {
                BucketName = _bucket, Key = key, ContentType = "application/zstd",
                InputStream = AsStream(compressedData)
            };

            request.Metadata.Add(RawSizeMetadata, size.ToString(CultureInfo.InvariantCulture));
            await TimedAsync("put_chunk", () => _s3.PutObjectAsync(request, cancellationToken));
        }

        public async Task<StoredChunk> GetChunkAsync(byte[] id, CancellationToken cancellationToken)
        {
            try
            {
                using GetObjectResponse response = await TimedAsync("get_chunk", () => _s3.GetObjectAsync(_bucket, ChunkKey(id), cancellationToken));
                if (response.ContentLength < 0 || response.ContentLength > ChunkCodec.MaximumCompressedSize)
                    throw new InvalidDataException("Stored chunk has an invalid size.");

                byte[] data = new byte[response.ContentLength];
                await response.ResponseStream.ReadExactlyAsync(data, cancellationToken);
                return new StoredChunk(data, ReadRawSize(response.Metadata));
            }
            catch (AmazonS3Exception exception) when (IsNotFound(exception))
            {
                throw new FileNotFoundException("Chunk not found.", exception);
            }
        }
        public async Task<FileManifest?> GetManifestAsync(string fileId, CancellationToken cancellationToken)
        {
            try
            {
                using GetObjectResponse response = await TimedAsync("get_manifest", () => _s3.GetObjectAsync(_bucket, ManifestKey(fileId), cancellationToken));
                return FileManifest.Parser.ParseFrom(response.ResponseStream);
            }
            catch (AmazonS3Exception exception) when (IsNotFound(exception))
            {
                return null;
            }
        }

        /// <summary>
        /// Runs one S3 call and records its duration and outcome (<c>vertex.storage.duration</c>).
        /// </summary>
        private static async Task<T> TimedAsync<T>(string operation, Func<Task<T>> call)
        {
            long start = Stopwatch.GetTimestamp();
            string outcome = "ok";
            try
            {
                return await call();
            }
            catch (AmazonS3Exception exception) when (IsNotFound(exception))
            {
                outcome = "not_found";
                throw;
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                throw;
            }
            catch
            {
                outcome = "error";
                throw;
            }
            finally
            {
                VertexMetrics.StorageDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, VertexMetrics.Tag("operation", operation), VertexMetrics.Tag("outcome", outcome));
            }
        }
        private static Stream AsStream(ReadOnlyMemory<byte> data)
        {
            return MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false)
                : new MemoryStream(data.ToArray(), false);
        }

        private static long ReadRawSize(MetadataCollection metadata)
        {
            string? value = metadata[RawSizeMetadata];
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size < 1 || size > ContentChunker.MaximumChunkSize)
                throw new InvalidDataException("Stored chunk is missing a valid raw size.");

            return size;
        }

        private static bool IsNotFound(AmazonS3Exception exception)
        {
            return exception.StatusCode == HttpStatusCode.NotFound || exception.ErrorCode == "NoSuchKey";
        }

        private static string ChunkKey(byte[] id)
        {
            return id.Length != ContentId.HashSize ? throw new ArgumentException("Chunk id must be a SHA-256 hash.", nameof(id)) : ShardedKey("chunks", ContentId.ToHex(id));
        }

        private static string ManifestKey(string fileId)
        {
            return !ContentId.IsValidFileId(fileId) ? throw new ArgumentException("File id must be a SHA-256 hex string.", nameof(fileId)) : ShardedKey("manifests", fileId.ToLowerInvariant());
        }

        private static string ShardedKey(string prefix, string hex)
        {
            return $"{prefix}/{hex[..2]}/{hex[2..4]}/{hex}";
        }
    }
}