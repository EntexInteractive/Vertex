// Copyright 2026 Entex Interactive

using Vertex.Cli.Net;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Branches
{
    public sealed class CreateBranchSettings : BranchSettings
    {
        [CommandOption("--path")] public string? Path { get; set; }
    }

    public sealed class BranchesCommand : AsyncCommand<CreateBranchSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, CreateBranchSettings settings, CancellationToken cancellationToken)
        {
            string server = settings.Server ?? "https://localhost:5000";
            string relativePath = settings.Path ?? Directory.GetCurrentDirectory();
            VConfig? config = VConfig.Parse(Path.Combine(relativePath, ".vxconfig"));
            string? projectName = settings.Project ?? config?.Project;
            if (string.IsNullOrWhiteSpace(projectName))
            {
                AnsiConsole.MarkupLine("[red]Project name is required.[/]");
                return 0;
            }

            using ProjectInterface projects = new(server);
            ServerResult<VProject> projectResult = await projects.GetProjectByNameAsync(projectName, cancellationToken);
            if (!projectResult.IsSuccessStatusCode && projectResult.Data == null)
            {
                AnsiConsole.MarkupLine($"[red]Failed to get project: {Markup.Escape(projectResult.Exception?.Message ?? "Unknown error")}[/]");
                return 0;
            }

            using BranchInterface branches = new(server);
            ServerResult<VBranch> result = await branches.CreateBranchAsync(projectResult.Data!.Id, settings.Branch ?? "main", cancellationToken: cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Failed to create branch: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"Branch '{Markup.Escape(result.Data?.Name ?? "")}' created successfully.");
            return 0;
        }
    }

    public sealed class DeleteBranchSettings : NetworkSettings
    {
        [CommandOption("--project|-p")] public string? Project { get; set; }
        [CommandOption("--branch|-b")] public string? Branch { get; set; }
    }

    public sealed class DeleteBranchCommand : Command<DeleteBranchSettings>
    {
        public override int Execute(CommandContext context, DeleteBranchSettings settings, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}