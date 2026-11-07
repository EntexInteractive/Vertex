// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.Net;
using Vertex.Server;

namespace Vertex.Cli.IO
{
    public class PushCommand : BaseIOCommand
    {
        public PushCommand() : base("push", "Pushes files to the primary server.")
        {
            this.SetAction(HandleCommand);
        }

        private async Task HandleCommand(ParseResult parse)
        {
            string path = Path.GetFullPath(parse.GetValue(PathOption) ?? Directory.GetCurrentDirectory());
            string address = parse.GetValue(ServerOption) ?? "http://localhost:5000";
            string[] files = File.Exists(path)
                ? [path]
                : Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray()
                    : throw new FileNotFoundException("The push path does not exist.", path);

            using FileTransferClient client = new(address);
            foreach (string file in files)
            {
                UploadFileResponse response = await client.UploadAsync(file);
                Terminal.WriteLine($"Uploaded {response.FileName} ({response.BytesReceived:N0} bytes), id {response.FileId}", ConsoleColor.Green);
            }
        }
    }
}
