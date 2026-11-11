// Copyright 2026 Entex Interactive

using Grpc.Core;
using Vertex.Core.Net.Interfaces;
using Vertex.Server.Storage;

namespace Vertex.Server.FileServices
{
    public class FileTransferService : Files.FilesBase
    {
        private const int MaximumChunkSize = 64 * 1024;
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

            string fileId = Guid.NewGuid().ToString("N");
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
            if (!Guid.TryParseExact(request.FileId, "N", out _)) throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid file id."));

            StoredFile stored;
            try
            {
                stored = await _storage.OpenReadAsync(request.FileId, context.CancellationToken);
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
                });

                byte[] buffer = new byte[MaximumChunkSize];
                int bytesRead;
                while ((bytesRead = await stored.Content.ReadAsync(buffer, context.CancellationToken)) > 0)
                {
                    await responseStream.WriteAsync(new DownloadFileResponse
                    {
                        Chunk = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead)
                    });
                }
            }
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
                if (request.Chunk.Length > MaximumChunkSize) throw new InvalidDataException("Upload chunk exceeds the server chunk limit.");
                yield return request.Chunk.Memory;
            }
        }
    }
}
