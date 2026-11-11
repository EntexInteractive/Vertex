// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Cli.Net;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Projects
{
    public sealed class CreateProjectSettings : NetworkSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; set; } = string.Empty;
    }

    public sealed class CreateProjectCommand : AsyncCommand<CreateProjectSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, CreateProjectSettings settings, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine(Markup.Escape(settings.Name));
            using ProjectInterface projectInterface = new(settings.ServerAddress);
            ServerResult<VProject> result = await projectInterface.CreateProjectAsync(settings.Name, cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Failed to create project: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                Log.Error(result.Exception, "Failed to create project");
                return 0;
            }

            AnsiConsole.MarkupLine($"Project '{Markup.Escape(result.Data?.Name ?? "")}' created successfully.");
            return 1;
        }
    }
}