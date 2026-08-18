// Copyright 2026 Entex Interactive

using Google.Protobuf;
using Grpc.Core;

namespace Vertex.Server.Services
{
    public class FileService : Files.FilesBase
    {
        private readonly ILogger<FileService> _logger;

        public FileService(ILogger<FileService> logger)
        {
            _logger = logger;
        }

        public override async Task<UploadFileResponse> Upload(IAsyncStreamReader<UploadFileRequest> requestStream,
            ServerCallContext context)
        {
            string? fileName = null;
            string? contentType = null;
            long fileSize = 0;
            long bytesReceived = 0;

            while (await requestStream.MoveNext(context.CancellationToken))
            {
                UploadFileRequest request = requestStream.Current;
                switch (request.DataCase)
                {
                    case UploadFileRequest.DataOneofCase.Metadata:
                        fileName = request.Metadata.FileName;
                        contentType = request.Metadata.ContentType;
                        fileSize = request.Metadata.FileSize;

                        _logger.LogInformation("Receiving {FileName} ({FileSize} bytes)", fileName, fileSize);
                        break;

                    case UploadFileRequest.DataOneofCase.Chunk:
                        ByteString chunk = request.Chunk;
                        bytesReceived += chunk.Length;

                        // Write chunk to disk/blob storage here.
                        break;
                }
            }

            return new UploadFileResponse
            {
                FileId = Guid.NewGuid().ToString(),
                FileName = fileName ?? "",
                BytesReceived = bytesReceived
            };
        }
    }
}