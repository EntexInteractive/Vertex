// Copyright 2026 Entex Interactive

using Grpc.Core;
using Grpc.Net.Client;
using Vertex.Server;

namespace Vertex.Core.Net
{
    /// <summary>Streams files to and from a Vertex server using bounded gRPC messages.</summary>
    public sealed class FileTransferClient : IDisposable
    {
        private const int ChunkSize = 256 * 1024;
        private readonly GrpcChannel _channel;
        private readonly Files.FilesClient _client;

        public FileTransferClient(string serverAddress)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serverAddress);
            _channel = GrpcChannel.ForAddress(serverAddress);
            _client = new Files.FilesClient(_channel);
        }

        /// <summary>Uploads a file and returns the server's assigned file identifier and byte count.</summary>
        public async Task<UploadFileResponse> UploadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            FileInfo info = new(filePath);
            if (!info.Exists) throw new FileNotFoundException("File to upload was not found.", filePath);

            using AsyncClientStreamingCall<UploadFileRequest, UploadFileResponse> call = _client.UploadAsync(cancellationToken: cancellationToken);
            await call.RequestStream.WriteAsync(new UploadFileRequest
            {
                Metadata = new FileMetadata { FileName = info.Name, ContentType = "application/octet-stream", FileSize = info.Length }
            }, cancellationToken);

            byte[] buffer = new byte[ChunkSize];
            await using FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                await call.RequestStream.WriteAsync(new UploadFileRequest { Chunk = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) }, cancellationToken);

            await call.RequestStream.CompleteAsync();
            return await call.ResponseAsync;
        }

        /// <summary>Downloads a file to a destination, publishing it only after the complete stream is received.</summary>
        public async Task<long> DownloadAsync(string fileId, string destinationPath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
            string destination = Path.GetFullPath(destinationPath);
            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = destination + ".download";

            try
            {
                using AsyncServerStreamingCall<DownloadFileResponse> call = _client.DownloadAsync(
                    new DownloadFileRequest { FileId = fileId }, cancellationToken: cancellationToken);

                await using FileStream output = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                FileMetadata? metadata = null;
                long bytesReceived = 0;
                while (await call.ResponseStream.MoveNext(cancellationToken))
                {
                    DownloadFileResponse response = call.ResponseStream.Current;
                    if (response.DataCase == DownloadFileResponse.DataOneofCase.Metadata)
                    {
                        if (metadata is not null || bytesReceived != 0) throw new InvalidDataException("Invalid download stream metadata.");
                        metadata = response.Metadata;
                        if (metadata.FileSize < 0) throw new InvalidDataException("Server reported an invalid file size.");
                    }
                    else if (response.DataCase == DownloadFileResponse.DataOneofCase.Chunk)
                    {
                        if (metadata is null || response.Chunk.Length > ChunkSize || bytesReceived > metadata.FileSize - response.Chunk.Length)
                            throw new InvalidDataException("Download stream exceeded its declared size or chunk limit.");

                        await output.WriteAsync(response.Chunk.Memory, cancellationToken);
                        bytesReceived += response.Chunk.Length;
                    }
                    else
                    {
                        throw new InvalidDataException("Download stream contained an empty message.");
                    }
                }

                if (metadata is null || bytesReceived != metadata.FileSize)
                    throw new InvalidDataException("Download ended before the complete file arrived.");

                await output.FlushAsync(cancellationToken);
                await output.DisposeAsync();
                File.Move(temporaryPath, destination, true);
                return bytesReceived;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        public void Dispose()
        {
            _channel.Dispose();
        }
    }
}