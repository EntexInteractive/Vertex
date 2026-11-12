// Copyright 2026 Entex Interactive

using System.Diagnostics;
using EntexInteractive.Extensions;
using Vertex.Core.Client.IO;
using Vertex.Core.Data;

namespace Vertex.Cli.Files
{
    public sealed class StatusCommand : AsyncCommand<FilesSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, FilesSettings settings, CancellationToken cancellationToken)
        {
            Stopwatch sw = Stopwatch.StartNew();
            IReadOnlyList<VChange> changedFiles = [];
            await AnsiConsole.Status().Start("Scanning for changes...", async _ =>
            {
                changedFiles = await FileSystemUtility.GetChangesAsync(settings.RootPath, Guid.Empty, settings.MaxConcurrency, settings.Force);
            });

            // Displays only 50 changes.
            int display = Math.Min(50, changedFiles.Count);
            for (int i = 0; i < display; i++)
            {
                VChange change = changedFiles[i];
                switch (change.Type)
                {
                    case ChangeType.Created:
                        AnsiConsole.MarkupLine($"[green]{Markup.Escape(change.File.RelativePath)}[/]");
                        break;

                    case ChangeType.Modified:
                        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(change.File.RelativePath)}[/]");
                        break;

                    case ChangeType.Deleted:
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(change.File.RelativePath)}[/]");
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            int remaining = changedFiles.Count - display;
            if (remaining > 0)
            {
                AnsiConsole.MarkupLine($"[red]{remaining:N0} change{(remaining == 1 ? "" : "s")} remaining...[/]");
            }

            AnsiConsole.MarkupLine(Markup.Escape($"\r\nFound {changedFiles.Count} changes in {sw.Elapsed}"));
            return 0;
        }
    }
}