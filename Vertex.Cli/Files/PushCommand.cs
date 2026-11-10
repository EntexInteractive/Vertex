// Copyright 2026 Entex Interactive

using Vertex.Core.Net.Interfaces;

namespace Vertex.Cli.Files
{
    public class PushCommand : BaseFilesCommand
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

            using FilesInterface client = new(address);
            /*foreach (string file in files)
            {
                UploadFileResponse response = await client.UploadAsync(file);
                Terminal.WriteLine($"Uploaded {response.FileName} ({response.BytesReceived:N0} bytes), id {response.FileId}", ConsoleColor.Green);
            }*/
        }
    }
}
