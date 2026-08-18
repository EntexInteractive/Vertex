// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Grpc.Core;
using Grpc.Net.Client;
using Vertex.Server;

namespace Vertex.Cli.IO
{
    public class PushCommand : Command
    {
        private readonly Option<string> _pathOption = new("--path", ["-p"]) { Description = "Path to a file or directory." };
        private readonly Option<string> _serverOption = new("--server") { Description = "The server address to connect to." };

        private readonly Files.FilesClient _client;
        private readonly GrpcChannel _channel;

        public PushCommand() : base("push", "Pushes files to the primary server.")
        {
            this.Add(_pathOption);
            this.Add(_serverOption);
            this.SetAction(HandleCommand);
        }

        private async Task HandleCommand(ParseResult parse)
        {
            string path = parse.GetValue(_pathOption) ?? Directory.GetCurrentDirectory();
            string address = parse.GetValue(_serverOption) ?? "https://localhost:5000";
            Terminal.WriteLine($"Scanning for file changes in {path}...", ConsoleColor.DarkGray);

            GrpcChannel channel = GrpcChannel.ForAddress(address);
            Files.FilesClient client = new Files.FilesClient(channel);

            string? file = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).FirstOrDefault();
            using AsyncClientStreamingCall<UploadFileRequest, UploadFileResponse> request = client.Upload();

            // Send metadata first.
            FileInfo fileInfo = new FileInfo(file);
            FileMetadata metadata = new FileMetadata { FileName = fileInfo.Name, ContentType = "application/octet-stream", FileSize = fileInfo.Length };
            await request.RequestStream.WriteAsync(new UploadFileRequest { Metadata = metadata });

            // Send the file in chunks.
            const int bufferSize = 64 * 1024;

            byte[] buffer = new byte[bufferSize];
            await using FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            int bytesRead;

            while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
            {
                await request.RequestStream.WriteAsync(
                    new UploadFileRequest
                    {
                        Chunk = Google.Protobuf.ByteString.CopyFrom(
                            buffer,
                            0,
                            bytesRead)
                    });
            }

            // Tell the server we're finished sending data.
            await request.RequestStream.CompleteAsync();

            UploadFileResponse response = await request.ResponseAsync;
            Terminal.WriteLine($"Uploaded {response.FileName} ({response.BytesReceived:N0} bytes)", ConsoleColor.Green);
        }
    }
}