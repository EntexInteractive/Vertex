// Copyright 2026 Entex Interactive

using System.Buffers;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Vertex.Server.Storage
{
    /// <summary>
    /// Represents asynchronous storage operations for writing and reading files identified by a storage key.
    /// </summary>
    public interface IFileStorage
    {
        Task WriteAsync(string key, string contentType, long expectedLength, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken cancellationToken);
        Task<StoredFile> OpenReadAsync(string key, CancellationToken cancellationToken);
        Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
    }

    public sealed class StoredFile(Stream content, long length, IDisposable owner) : IAsyncDisposable
    {
        public Stream Content { get; } = content;
        public long Length { get; } = length;

        public async ValueTask DisposeAsync()
        {
            await Content.DisposeAsync();
            owner.Dispose();
        }
    }

    public sealed class S3FileStorage : IFileStorage
    {
        private const int MinimumPartSize = 8 * 1024 * 1024;
        private const int MaximumParts = 10_000;
        private const long MaximumPartSize = 5L * 1024 * 1024 * 1024;
        private const long MaximumObjectSize = MaximumPartSize * MaximumParts;
        private const int MaximumBufferedPartSize = 256 * 1024 * 1024;
        private const int PartUploadConcurrency = 4;
        private const string LegacyObjectPrefix = "files";
        private readonly IAmazonS3 _s3;
        private readonly string _bucket;

        public S3FileStorage(IAmazonS3 s3, ServerSettings settings)
        {
            _s3 = s3;
            _bucket = settings.Storage.Bucket;
        }

        public async Task WriteAsync(string key, string contentType, long expectedLength, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken cancellationToken)
        {
            if (expectedLength < 0 || expectedLength > MaximumObjectSize) throw new InvalidDataException("File size is outside S3's supported object size range.");

            string objectKey = GetObjectKey(key);
            if (expectedLength == 0)
            {
                await foreach (ReadOnlyMemory<byte> incoming in chunks.WithCancellation(cancellationToken))
                    if (!incoming.IsEmpty)
                        throw new InvalidDataException("Upload exceeded its declared length.");

                await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = objectKey, ContentType = contentType, InputStream = new MemoryStream() }, cancellationToken);
                return;
            }

            long partSize = Math.Max(MinimumPartSize, (expectedLength + MaximumParts - 1) / MaximumParts);
            if (partSize > MaximumBufferedPartSize) throw new InvalidDataException("File requires S3 multipart parts larger than the server supports.");

            // Parts are assembled in pooled memory (no temp-file round trip) and uploaded in the background,
            // so the client stream keeps flowing while earlier parts are sent to S3.
            int bufferSize = (int)Math.Min(partSize, expectedLength);
            using CancellationTokenSource partsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using SemaphoreSlim uploadSlots = new(PartUploadConcurrency);
            List<Task<PartETag>> partUploads = new();
            string? uploadId = null;
            byte[]? part = null;
            int partLength = 0;
            int partNumber = 0;
            long received = 0;
            bool completed = false;

            try
            {
                part = ArrayPool<byte>.Shared.Rent(bufferSize);
                await foreach (ReadOnlyMemory<byte> incoming in chunks.WithCancellation(cancellationToken))
                {
                    received = checked(received + incoming.Length);
                    if (received > expectedLength) throw new InvalidDataException("Upload exceeded its declared length.");

                    ReadOnlyMemory<byte> remaining = incoming;
                    while (!remaining.IsEmpty)
                    {
                        int count = Math.Min(bufferSize - partLength, remaining.Length);
                        remaining.Span[..count].CopyTo(part.AsSpan(partLength));
                        partLength += count;
                        remaining = remaining[count..];

                        // The final part is sent after the stream ends so the declared length can be verified first.
                        if (partLength == bufferSize && received < expectedLength)
                        {
                            uploadId ??= (await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
                            {
                                BucketName = _bucket, Key = objectKey, ContentType = contentType
                            }, cancellationToken)).UploadId;

                            // Waiting for a slot bounds memory to (concurrency + 1) parts per upload and applies
                            // back-pressure to the client when S3 is slower than the network.
                            await uploadSlots.WaitAsync(cancellationToken);
                            ThrowIfAnyPartFailed(partUploads);
                            byte[] full = part;
                            part = null;
                            partUploads.Add(UploadPartAsync(objectKey, uploadId, ++partNumber, full, partLength, uploadSlots, partsCts.Token));
                            part = ArrayPool<byte>.Shared.Rent(bufferSize);
                            partLength = 0;
                        }
                    }
                }

                if (received != expectedLength) throw new InvalidDataException("Upload ended before its declared length.");
                if (uploadId is null)
                {
                    await _s3.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = _bucket, Key = objectKey, ContentType = contentType,
                        InputStream = new MemoryStream(part, 0, partLength, false)
                    }, cancellationToken);

                    completed = true;
                    return;
                }

                await uploadSlots.WaitAsync(cancellationToken);
                byte[] last = part;
                part = null;
                partUploads.Add(UploadPartAsync(objectKey, uploadId, ++partNumber, last, partLength, uploadSlots, partsCts.Token));

                PartETag[] parts = await Task.WhenAll(partUploads);
                await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = _bucket, Key = objectKey, UploadId = uploadId, PartETags = parts.ToList()
                }, cancellationToken);

                completed = true;
            }
            finally
            {
                if (!completed)
                {
                    await partsCts.CancelAsync();
                    try
                    {
                        await Task.WhenAll(partUploads);
                    }
                    catch
                    {
                        /* Preserve the original transfer error. */
                    }
                }

                if (part is not null) ArrayPool<byte>.Shared.Return(part);
                if (!completed && uploadId is not null)
                {
                    try
                    {
                        await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = objectKey, UploadId = uploadId }, CancellationToken.None);
                    }
                    catch
                    {
                        /* Preserve the original transfer error; bucket lifecycle rules should clear abandoned uploads. */
                    }
                }
            }
        }

        public async Task<StoredFile> OpenReadAsync(string key, CancellationToken cancellationToken)
        {
            string objectKey = GetObjectKey(key);
            try
            {
                GetObjectResponse response = await _s3.GetObjectAsync(_bucket, objectKey, cancellationToken);
                return new StoredFile(response.ResponseStream, response.ContentLength, response);
            }
            catch (AmazonS3Exception exception) when (IsNotFound(exception))
            {
                try
                {
                    // Keep file IDs uploaded before the object-key layout change readable.
                    GetObjectResponse legacyResponse = await _s3.GetObjectAsync(_bucket, $"{LegacyObjectPrefix}/{key}", cancellationToken);
                    return new StoredFile(legacyResponse.ResponseStream, legacyResponse.ContentLength, legacyResponse);
                }
                catch (AmazonS3Exception legacyException) when (IsNotFound(legacyException))
                {
                    throw new FileNotFoundException("File not found.", legacyException);
                }
            }
        }

        public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
        {
            foreach (string objectKey in new[] { GetObjectKey(key), $"{LegacyObjectPrefix}/{key}" })
            {
                try
                {
                    await _s3.GetObjectMetadataAsync(_bucket, objectKey, cancellationToken);
                    return true;
                }
                catch (AmazonS3Exception exception) when (IsNotFound(exception))
                {
                    /* Try the next key layout. */
                }
            }

            return false;
        }

        /// <summary>
        /// Uploads one part from a pooled buffer, then returns the buffer and releases its concurrency slot.
        /// </summary>
        private async Task<PartETag> UploadPartAsync(string objectKey, string uploadId, int partNumber, byte[] buffer, int length, SemaphoreSlim slots, CancellationToken cancellationToken)
        {
            try
            {
                UploadPartResponse uploaded = await _s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket, Key = objectKey, UploadId = uploadId, PartNumber = partNumber,
                    PartSize = length, InputStream = new MemoryStream(buffer, 0, length, false)
                }, cancellationToken);

                return new PartETag(partNumber, uploaded.ETag);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                slots.Release();
            }
        }

        private static void ThrowIfAnyPartFailed(List<Task<PartETag>> partUploads)
        {
            foreach (Task<PartETag> upload in partUploads)
                if (upload.IsFaulted || upload.IsCanceled)
                    upload.GetAwaiter().GetResult();
        }

        private static bool IsNotFound(AmazonS3Exception exception)
        {
            return exception.StatusCode == HttpStatusCode.NotFound || exception.ErrorCode == "NoSuchKey";
        }

        private static string GetObjectKey(string key)
        {
            bool legacyId = Guid.TryParseExact(key, "N", out _);
            bool checksumId = key.Length == 64 && key.All(Uri.IsHexDigit);
            if (!legacyId && !checksumId) throw new ArgumentException("Object key must be a compact file identifier or SHA-256 checksum.", nameof(key));
            string fileId = key.ToLowerInvariant();
            return $"objects/{fileId[..2]}/{fileId[2..4]}/{fileId}";
        }
    }
}
