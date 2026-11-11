// Copyright 2026 Entex Interactive

using Spectre.Console;
using Spectre.Console.Cli;
using Vertex.Core.Client.IO;
using Vertex.Core.IO;

namespace Vertex.Cli.Files
{
    public sealed class StatusCommand : Command<FilesSettings>
    {
        public override int Execute(CommandContext context, FilesSettings settings, CancellationToken cancellationToken)
        {
            string path = settings.Path ?? Directory.GetCurrentDirectory();
            IEnumerable<string> localFiles = FileSystemUtility.IsDirectory(path) ? FileSystemUtility.GetFiles(path, FileSystemUtility.GetIgnoredEntries(path)) : [path];

            Table table = new Table().Border(TableBorder.Rounded).AddColumn("Changed files");
            foreach (string file in localFiles) table.AddRow(Markup.Escape(file));
            AnsiConsole.Write(table);
            return 0;
        }
    }
}