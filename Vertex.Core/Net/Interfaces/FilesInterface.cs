// Copyright 2026 Entex Interactive

using Grpc.Core;
using Vertex.Core.Data;
using Vertex.Server;

namespace Vertex.Core.Net.Interfaces
{
    public class FilesInterface : BaseInterface
    {
        public int ChunkSize { get; set; } = 256 * 1024;
        public Files.FilesClient FilesClient { get; }
        
        public FilesInterface(string url) : base(url)
        {
            FilesClient = new Files.FilesClient(GrpcChannel);
        }
        
        /// <summary>
        /// Uploads a file and returns the server's assigned file identifier and byte count.
        /// </summary>
        public async Task<UploadFileResponse> UploadAsync(VFile file, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(file);
            using AsyncClientStreamingCall<UploadFileRequest, UploadFileResponse> call = FilesClient.UploadAsync(cancellationToken: ct);
            await call.RequestStream.WriteAsync(new UploadFileRequest
            {
                Metadata = new FileMetadata { FileName = file.Name, ContentType = "application/octet-stream", FileSize = file.Length }
            }, ct);

            byte[] buffer = new byte[ChunkSize];
            await using FileStream input = new(file.Fullname, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
                await call.RequestStream.WriteAsync(new UploadFileRequest { Chunk = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) }, ct);

            await call.RequestStream.CompleteAsync();
            return await call.ResponseAsync;
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
            string temporaryPath = destination + ".download";

            try
            {
                using AsyncServerStreamingCall<DownloadFileResponse> call = FilesClient.DownloadAsync(new DownloadFileRequest { FileId = fileId }, cancellationToken: ct);

                await using FileStream output = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                FileMetadata? metadata = null;
                long bytesReceived = 0;
                while (await call.ResponseStream.MoveNext(ct))
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

                        await output.WriteAsync(response.Chunk.Memory, ct);
                        bytesReceived += response.Chunk.Length;
                    }
                    else
                    {
                        throw new InvalidDataException("Download stream contained an empty message.");
                    }
                }

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
    }
}