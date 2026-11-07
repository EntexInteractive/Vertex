// Copyright 2026 Entex Interactive

using Grpc.Core;

namespace Vertex.Server.Services
{
    public class FileService : Files.FilesBase
    {
        private const int BufferSize = 256 * 1024;
        private readonly ILogger<FileService> _logger;
        private readonly string _storageDirectory;

        public FileService(ILogger<FileService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _storageDirectory = Path.GetFullPath(configuration["Vertex:StoragePath"] ?? Environment.GetEnvironmentVariable("VERTEX_STORAGE_PATH") ?? Path.Combine(AppContext.BaseDirectory, "data", "files"));
            Directory.CreateDirectory(_storageDirectory);
        }

        public override async Task<UploadFileResponse> UploadAsync(IAsyncStreamReader<UploadFileRequest> requestStream, ServerCallContext context)
        {
            FileMetadata? metadata = null;
            string? temporaryPath = null;
            string? fileId = null;
            FileStream? outputStream = null;
            long bytesReceived = 0;

            try
            {
                while (await requestStream.MoveNext(context.CancellationToken))
                {
                    UploadFileRequest request = requestStream.Current;
                    switch (request.DataCase)
                    {
                        case UploadFileRequest.DataOneofCase.Metadata:
                            if (metadata is not null || bytesReceived != 0)
                                throw new RpcException(new Status(StatusCode.InvalidArgument, "File metadata must be the first and only metadata message."));

                            metadata = request.Metadata;
                            if (metadata.FileSize < 0 || string.IsNullOrWhiteSpace(metadata.FileName))
                                throw new RpcException(new Status(StatusCode.InvalidArgument, "A file name and nonnegative file size are required."));

                            metadata.FileName = Path.GetFileName(metadata.FileName);
                            fileId = Guid.NewGuid().ToString("N");
                            temporaryPath = GetFilePath(fileId) + ".upload";
                            outputStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            _logger.LogInformation("Receiving {FileName} ({FileSize} bytes)", metadata.FileName, metadata.FileSize);
                            break;

                        case UploadFileRequest.DataOneofCase.Chunk:
                            if (metadata is null || temporaryPath is null)
                                throw new RpcException(new Status(StatusCode.InvalidArgument, "File metadata must be sent before file data."));

                            if (request.Chunk.Length > BufferSize || bytesReceived > metadata.FileSize - request.Chunk.Length)
                                throw new RpcException(new Status(StatusCode.InvalidArgument, "File data exceeds the declared size or chunk limit."));

                            await outputStream!.WriteAsync(request.Chunk.Memory, context.CancellationToken);
                            bytesReceived += request.Chunk.Length;
                            break;

                        default:
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "Upload message must contain metadata or file data."));
                    }
                }

                if (metadata is null || temporaryPath is null || fileId is null)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Upload did not contain file metadata."));

                if (bytesReceived != metadata.FileSize)
                    throw new RpcException(new Status(StatusCode.DataLoss, $"Expected {metadata.FileSize} bytes but received {bytesReceived}."));

                await outputStream!.FlushAsync(context.CancellationToken);
                await outputStream.DisposeAsync();
                outputStream = null;
                File.Move(temporaryPath, GetFilePath(fileId));
                temporaryPath = null;
                return new UploadFileResponse { FileId = fileId, FileName = metadata.FileName, BytesReceived = bytesReceived };
            }
            finally
            {
                if (outputStream is not null) await outputStream.DisposeAsync();
                if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        public override async Task DownloadAsync(DownloadFileRequest request, IServerStreamWriter<DownloadFileResponse> responseStream, ServerCallContext context)
        {
            if (!Guid.TryParseExact(request.FileId, "N", out _))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid file id."));

            string filePath = GetFilePath(request.FileId);
            if (!File.Exists(filePath)) throw new RpcException(new Status(StatusCode.NotFound, "File not found."));

            await using FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await responseStream.WriteAsync(new DownloadFileResponse
            {
                Metadata = new FileMetadata { FileName = request.FileId, ContentType = "application/octet-stream", FileSize = input.Length }
            });

            byte[] buffer = new byte[BufferSize];
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, context.CancellationToken)) > 0)
            {
                await responseStream.WriteAsync(new DownloadFileResponse
                {
                    Chunk = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead)
                });
            }
        }

        private string GetFilePath(string fileId)
        {
            return Path.Combine(_storageDirectory, $"{fileId}.blob");
        }
    }
}
