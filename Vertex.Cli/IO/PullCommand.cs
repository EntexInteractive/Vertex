// Copyright 2026 Entex Interactive

using Vertex.Core.Net;

namespace Vertex.Cli.IO
{
    public class PullCommand : Command
    {
        private readonly Option<string> _fileIdOption = new("--file-id") { Description = "The file id returned by push." };
        private readonly Option<string> _pathOption = new("--path", ["-p"]) { Description = "Destination file path." };
        private readonly Option<string> _serverOption = new("--server") { Description = "The server address to connect to." };

        public PullCommand() : base("pull", "Downloads a file from the primary server.")
        {
            this.Add(_fileIdOption);
            this.Add(_pathOption);
            this.Add(_serverOption);
            this.SetAction(HandleCommandAsync);
        }

        private async Task HandleCommandAsync(ParseResult parse)
        {
            string fileId = parse.GetValue(_fileIdOption) ?? throw new ArgumentException("--file-id is required.");
            string destination = Path.GetFullPath(parse.GetValue(_pathOption) ?? fileId + ".blob");
            string address = parse.GetValue(_serverOption) ?? "https://localhost:5000";
            using FileTransferClient client = new(address);
            long bytesReceived = await client.DownloadAsync(fileId, destination);
            Console.WriteLine($"Downloaded {bytesReceived:N0} bytes to {destination}");
        }
    }
}