// Copyright 2026 Entex Interactive

using Amazon.S3;
using Amazon.S3.Model;
using System.Net;

namespace Vertex.Server.Services;

public interface IFileStorage
{
    Task WriteAsync(string key, string contentType, long expectedLength, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken cancellationToken);
    Task<StoredFile> OpenReadAsync(string key, CancellationToken cancellationToken);
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
        if (expectedLength < 0 || expectedLength > MaximumObjectSize)
            throw new InvalidDataException("File size is outside S3's supported object size range.");

        string objectKey = GetObjectKey(key);
        if (expectedLength == 0)
        {
            await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = objectKey, ContentType = contentType, InputStream = new MemoryStream() }, cancellationToken);
            return;
        }

        long partSize = Math.Max(MinimumPartSize, (expectedLength + MaximumParts - 1) / MaximumParts);
        if (partSize > MaximumPartSize) throw new InvalidDataException("File requires S3 multipart parts larger than the supported limit.");

        string? uploadId = null;
        List<PartETag> parts = new();
        string temporaryPartPath = Path.Combine(Path.GetTempPath(), $"vertex-s3-part-{Guid.NewGuid():N}.tmp");
        long received = 0;
        int partNumber = 0;
        try
        {
            await using FileStream part = new(temporaryPartPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await foreach (ReadOnlyMemory<byte> incoming in chunks.WithCancellation(cancellationToken))
            {
                received = checked(received + incoming.Length);
                if (received > expectedLength) throw new InvalidDataException("Upload exceeded its declared length.");
                ReadOnlyMemory<byte> remaining = incoming;
                while (!remaining.IsEmpty)
                {
                    int count = (int)Math.Min(partSize - part.Length, remaining.Length);
                    await part.WriteAsync(remaining[..count], cancellationToken);
                    remaining = remaining[count..];
                    if (part.Length == partSize)
                    {
                        uploadId ??= (await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
                        {
                            BucketName = _bucket, Key = objectKey, ContentType = contentType
                        }, cancellationToken)).UploadId;

                        partNumber++;
                        part.Position = 0;
                        UploadPartResponse uploaded = await _s3.UploadPartAsync(new UploadPartRequest
                        {
                            BucketName = _bucket, Key = objectKey, UploadId = uploadId, PartNumber = partNumber,
                            PartSize = part.Length, InputStream = part
                        }, cancellationToken);

                        parts.Add(new PartETag(partNumber, uploaded.ETag));
                        part.Position = 0;
                        part.SetLength(0);
                    }
                }
            }

            if (received != expectedLength) throw new InvalidDataException("Upload ended before its declared length.");
            if (uploadId is null)
            {
                part.Position = 0;
                await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = objectKey, ContentType = contentType, InputStream = part }, cancellationToken);
                return;
            }

            if (part.Length > 0)
            {
                partNumber++;
                part.Position = 0;
                UploadPartResponse uploaded = await _s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket, Key = objectKey, UploadId = uploadId, PartNumber = partNumber,
                    PartSize = part.Length, InputStream = part
                }, cancellationToken);

                parts.Add(new PartETag(partNumber, uploaded.ETag));
            }

            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket, Key = objectKey, UploadId = uploadId, PartETags = parts
            }, cancellationToken);

            uploadId = null;
        }
        finally
        {
            if (uploadId is not null)
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

            if (File.Exists(temporaryPartPath)) File.Delete(temporaryPartPath);
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

    private static bool IsNotFound(AmazonS3Exception exception)
    {
        return exception.StatusCode == HttpStatusCode.NotFound || exception.ErrorCode == "NoSuchKey";
    }

    private static string GetObjectKey(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("Object key must be a compact file identifier.", nameof(key));
        string fileId = key.ToLowerInvariant();
        return $"objects/{fileId[..2]}/{fileId[2..4]}/{fileId}";
    }
}
