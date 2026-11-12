// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Branches;
using Vertex.Core.Client.IO;
using Vertex.Core.Client.IO.IgnoreTemplates;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Workspaces
{
    public sealed class InitSettings : BranchSettings
    {
        [CommandOption("--template")]
        [Description("Starter .vxignore to write (e.g. 'unreal'), or 'none'. Detected from the folder by default.")]
        public string? Template { get; set; }
    }

    /// <summary>
    /// Creates a new project with a default branch on the server, then turns the folder into its workspace.
    /// </summary>
    /// <remarks>
    /// The server comes first: if it can't be reached or the name is taken, nothing is created locally. To link a
    /// folder to a project that already exists, use <c>vx fetch</c>.
    /// </remarks>
    public sealed class InitCommand : AsyncCommand<InitSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, InitSettings settings, CancellationToken cancellationToken)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.RootPath));
            Workspace? existing = Workspace.Find(root);
            if (existing is not null)
            {
                AnsiConsole.MarkupLine($"[red]'{Markup.Escape(existing.Root)}' is already a Vertex workspace.[/]");
                return 1;
            }

            IgnoreTemplate? template = null;
            if (settings.Template is not null && !settings.Template.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                template = IgnoreTemplate.Find(settings.Template);
                if (template is null)
                {
                    string available = string.Join(", ", IgnoreTemplate.All.Select(t => t.Name).Append("none"));
                    AnsiConsole.MarkupLine($"[red]Unknown template '{Markup.Escape(settings.Template)}'. Available: {Markup.Escape(available)}.[/]");
                    return 1;
                }
            }
            else if (settings.Template is null)
            {
                template = IgnoreTemplate.DetectFor(root);
            }

            string projectName = settings.Project ?? new DirectoryInfo(root).Name;
            string branchName = settings.Branch ?? "main";

            using ProjectInterface projects = new(settings.ServerAddress);
            ServerResult<CreateProjectResponse> result = await projects.CreateProjectAsync(projectName, branchName, cancellationToken);
            if (result.StatusCode == 409)
            {
                AnsiConsole.MarkupLine($"[red]Project '{Markup.Escape(projectName)}' already exists. Use 'vx fetch {Markup.Escape(projectName)}' to link this folder to it.[/]");
                return 1;
            }

            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Could not create the project: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                Log.Error(result.Exception, "Failed to create project");
                return 1;
            }

            CreateProjectResponse created = result.Data!;
            Workspace workspace;
            try
            {
                workspace = Workspace.Initialize(root, new WorkspaceConfig
                {
                    Address = settings.ServerAddress,
                    Project = created.Project.Name,
                    ProjectId = created.Project.Id,
                    Branch = created.DefaultBranch.Name,
                    BranchId = created.DefaultBranch.Id
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[red]Project '{Markup.Escape(created.Project.Name)}' was created on the server, but the workspace could not be created: {Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine($"[grey]Fix the problem, then run 'vx fetch {Markup.Escape(created.Project.Name)}' here to link it.[/]");
                return 1;
            }

            AnsiConsole.MarkupLine($"Created project [purple]{Markup.Escape(created.Project.Name)}[/] with branch [purple]{Markup.Escape(created.DefaultBranch.Name)}[/].");
            AnsiConsole.MarkupLine($"Initialized workspace in [purple]{Markup.Escape(workspace.MetadataDirectory)}[/]");

            if (template is not null)
            {
                if (File.Exists(Path.Combine(root, IgnoreRules.FileName)))
                {
                    AnsiConsole.MarkupLine($"[grey]Kept the existing {IgnoreRules.FileName}.[/]");
                }
                else
                {
                    template.WriteTo(root);
                    AnsiConsole.MarkupLine($"Wrote a {Markup.Escape(template.DisplayName)} {IgnoreRules.FileName}. Review it before your first submit.");
                }
            }

            return 0;
        }
    }
}
