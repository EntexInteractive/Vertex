// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.IO;

namespace Vertex.Cli.Files
{
    public class ChangesCommand : BaseFilesCommand
    {
        public ChangesCommand() : base("changes", "Gets a list of changed files.")
        {
            this.SetAction(HandleCommandAsync);
        }

        private void HandleCommandAsync(ParseResult arg)
        {
            string path = arg.GetValue(PathOption) ?? Directory.GetCurrentDirectory();
            int threads = arg.GetValue(ParallelOption) <= 0 ? Environment.ProcessorCount : arg.GetValue(ParallelOption);
            
            Terminal.WriteLine($"Using path: {path}");

            Terminal.WriteLine($"Getting changes with {threads} threads...");

            IEnumerable<string> localFiles = FileSystemUtility.IsDirectory(path) ? FileSystemUtility.GetFiles(path, FileSystemUtility.GetIgnoredEntries(path)) : [path];
            Terminal.WriteArray(path, localFiles);
        }
    }
}