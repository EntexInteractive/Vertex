// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.Client.IO;
using Vertex.Core.IO;

namespace Vertex.Cli.Files
{
    public class ChangesCommand : BaseFilesCommand
    {
        public ChangesCommand() : base("changes", "Gets a list of changed files.")
        {
            SetAction(HandleCommandAsync);
            Add(ServerOption);
            Add(ProjectOption);
            Add(BranchOption);
            Add(PathOption);
        }

        private void HandleCommandAsync(ParseResult arg)
        {
            string path = arg.GetValue(PathOption) ?? Directory.GetCurrentDirectory();

            IEnumerable<string> localFiles = FileSystemUtility.IsDirectory(path) ? FileSystemUtility.GetFiles(path, FileSystemUtility.GetIgnoredEntries(path)) : [path];
            Terminal.WriteArray(path, localFiles);
        }
    }
}