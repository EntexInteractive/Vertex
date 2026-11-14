// Copyright 2026 Entex Interactive

using Vertex.Cli.Net;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Projects
{
    /// <summary>
    /// The project a command acts on.
    /// </summary>
    public sealed record ResolvedProject(Guid Id, string Name);

    public static class ProjectResolver
    {
        /// <summary>
        /// Uses <paramref name="projectName"/> when given (one server lookup), otherwise the current workspace's
        /// project (no lookup).
        /// </summary>
        /// <returns>The project, or null after printing why it couldn't be found.</returns>
        public static async Task<ResolvedProject?> ResolveAsync(NetworkSettings settings, string? projectName, VWorkspace? workspace, CancellationToken cancellationToken)
        {
            if (projectName is null)
            {
                if (workspace?.Config.ProjectId is Guid id) return new ResolvedProject(id, workspace.Config.Project ?? id.ToString());
                AnsiConsole.MarkupLine("[red]Not inside a Vertex workspace. Run this from a workspace or pass --project.[/]");
                return null;
            }

            using ProjectInterface projects = new(settings.ServerAddress, settings.Token);
            ServerResult<VProject> result = await projects.GetProjectByNameAsync(projectName, cancellationToken);
            if (result.IsSuccessStatusCode) return new ResolvedProject(result.Data!.Id, result.Data.Name);

            if (result.StatusCode == 404)
            {
                AnsiConsole.MarkupLine($"[red]Project '{Markup.Escape(projectName)}' does not exist.[/]");
                return null;
            }

            ServerErrors.Print("Could not get the project", result);
            return null;
        }
    }
}
