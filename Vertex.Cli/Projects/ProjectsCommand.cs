// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Cli.Net;
using Vertex.Core.Net.Responses;
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
            using ProjectInterface projectInterface = new(settings.ServerAddress, settings.Token);
            ServerResult<CreateProjectResponse> result = await projectInterface.CreateProjectAsync(settings.Name, cancellationToken: cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Failed to create project: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                Log.Error(result.Exception, "Failed to create project");
                return 1;
            }

            AnsiConsole.MarkupLine($"Project '{Markup.Escape(result.Data!.Project.Name)}' created with branch '{Markup.Escape(result.Data.DefaultBranch.Name)}'.");
            return 0;
        }
    }
}