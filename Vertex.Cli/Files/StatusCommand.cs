// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.Client.IO;
using Vertex.Core.Data;

namespace Vertex.Cli.Files
{
    public sealed class StatusCommand : Command<FilesSettings>
    {
        public override int Execute(CommandContext context, FilesSettings settings, CancellationToken cancellationToken)
        {
            string rootPath = Path.GetFullPath(settings.RootPath);

            IEnumerable<string> files = FileSystemUtility.GetFiles(rootPath, FileSystemUtility.GetIgnoredEntries(rootPath));
            foreach (string file in files)
            {
                VFile vf = new(rootPath, file, Guid.Empty);
                Console.WriteLine(vf.RelativePath);
            }

            //IReadOnlyList<VChange> changedFiles = FileSystemUtility.GetChanges(settings.Path, settings.MaxConcurrency);
            //Terminal.WriteLine($"Found {changedFiles.Count} changed files.");
            return 0;
        }
    }
}