// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;
using Vertex.Cli.Projects;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;
using Vertex.Core.Security;

namespace Vertex.Cli.Access
{
    public class AccessSettings : NetworkSettings
    {
        [CommandOption("--project|-p")]
        [Description("The project. Defaults to the current workspace's project.")]
        public string? Project { get; set; }
    }

    public sealed class GrantSettings : AccessSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("A user or group.")]
        public string Name { get; set; } = string.Empty;

        [CommandArgument(1, "<level>")]
        [Description("read (sync and view history), write (submit and lock) or admin (manage access and branches).")]
        public string Level { get; set; } = string.Empty;
    }

    public sealed class RevokeSettings : AccessSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("A user or group.")]
        public string Name { get; set; } = string.Empty;
    }

    public sealed class VisibilitySettings : AccessSettings
    {
        [CommandArgument(0, "<visibility>")]
        [Description("internal (every logged-in user can read) or private (grants only).")]
        public string Visibility { get; set; } = string.Empty;
    }

    /// <summary>
    /// <c>vx access</c>: shows who can do what in the project.
    /// </summary>
    public sealed class ListAccessCommand : AsyncCommand<AccessSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, AccessSettings settings, CancellationToken cancellationToken)
        {
            ResolvedProject? project = await ProjectResolver.ResolveAsync(settings, settings.Project, VWorkspace.Find(Directory.GetCurrentDirectory()), cancellationToken);
            if (project is null) return 1;

            using ProjectInterface projects = new(settings.ServerAddress, settings.Token);
            ServerResult<VProject> details = await projects.GetProjectByIdAsync(project.Id.ToString(), cancellationToken);
            if (!details.IsSuccessStatusCode) return ServerErrors.Print("Could not get the project", details);

            using AccessInterface access = new(settings.ServerAddress, settings.Token);
            ServerResult<IReadOnlyList<VAccessGrant>> result = await access.ListGrantsAsync(project.Id, cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not list access", result);

            string visibility = details.Data!.Visibility == ProjectVisibility.Internal
                ? "internal [grey](every logged-in user can read it)[/]"
                : "private [grey](only the users and groups below)[/]";

            AnsiConsole.MarkupLine($"[purple]{Markup.Escape(details.Data.Name)}[/] is {visibility}");
            foreach (VAccessGrant grant in result.Data!)
            {
                string kind = grant.Kind == PrincipalKind.Group ? " [grey](group)[/]" : "";
                AnsiConsole.MarkupLine($"  {grant.Level.ToString().ToLowerInvariant(),-6} {Markup.Escape(grant.Name)}{kind}");
            }

            if (result.Data.Count == 0) AnsiConsole.MarkupLine("[grey]  No grants. Add one with 'vx access grant <user|group> <read|write|admin>'.[/]");
            return 0;
        }
    }

    /// <summary>
    /// <c>vx access grant &lt;user|group&gt; &lt;level&gt;</c>: sets a level, replacing any previous one. Project admins only.
    /// </summary>
    public sealed class GrantAccessCommand : AsyncCommand<GrantSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, GrantSettings settings, CancellationToken cancellationToken)
        {
            if (!Enum.TryParse(settings.Level, true, out AccessLevel level) || level == AccessLevel.None || !Enum.IsDefined(level))
            {
                AnsiConsole.MarkupLine($"[red]'{Markup.Escape(settings.Level)}' isn't a level. Use read, write or admin.[/]");
                return 1;
            }

            ResolvedProject? project = await ProjectResolver.ResolveAsync(settings, settings.Project, VWorkspace.Find(Directory.GetCurrentDirectory()), cancellationToken);
            if (project is null) return 1;

            using AccessInterface access = new(settings.ServerAddress, settings.Token);
            ServerResult<VAccessGrant> result = await access.GrantAsync(project.Id, settings.Name, level, cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not grant access", result);

            VAccessGrant grant = result.Data!;
            string kind = grant.Kind == PrincipalKind.Group ? "Group" : "User";
            AnsiConsole.MarkupLine($"{kind} [purple]{Markup.Escape(grant.Name)}[/] now has {grant.Level.ToString().ToLowerInvariant()} access to [purple]{Markup.Escape(project.Name)}[/].");
            return 0;
        }
    }

    /// <summary>
    /// <c>vx access revoke &lt;user|group&gt;</c>. Project admins only.
    /// </summary>
    public sealed class RevokeAccessCommand : AsyncCommand<RevokeSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, RevokeSettings settings, CancellationToken cancellationToken)
        {
            ResolvedProject? project = await ProjectResolver.ResolveAsync(settings, settings.Project, VWorkspace.Find(Directory.GetCurrentDirectory()), cancellationToken);
            if (project is null) return 1;

            using AccessInterface access = new(settings.ServerAddress, settings.Token);
            ServerResult<bool> result = await access.RevokeAsync(project.Id, settings.Name, cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not revoke access", result);

            AnsiConsole.MarkupLine(result.Data
                ? $"Revoked [purple]{Markup.Escape(settings.Name)}[/]'s access to [purple]{Markup.Escape(project.Name)}[/]."
                : $"[grey]{Markup.Escape(settings.Name)} had no grant in {Markup.Escape(project.Name)}.[/]");

            return 0;
        }
    }

    /// <summary>
    /// <c>vx access visibility &lt;internal|private&gt;</c>. Project admins only.
    /// </summary>
    public sealed class SetVisibilityCommand : AsyncCommand<VisibilitySettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, VisibilitySettings settings, CancellationToken cancellationToken)
        {
            if (!Enum.TryParse(settings.Visibility, true, out ProjectVisibility visibility) || !Enum.IsDefined(visibility))
            {
                AnsiConsole.MarkupLine($"[red]'{Markup.Escape(settings.Visibility)}' isn't a visibility. Use internal or private.[/]");
                return 1;
            }

            ResolvedProject? project = await ProjectResolver.ResolveAsync(settings, settings.Project, VWorkspace.Find(Directory.GetCurrentDirectory()), cancellationToken);
            if (project is null) return 1;

            using AccessInterface access = new(settings.ServerAddress, settings.Token);
            ServerResult<VProject> result = await access.SetVisibilityAsync(project.Id, visibility, cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not change the visibility", result);

            AnsiConsole.MarkupLine(visibility == ProjectVisibility.Internal
                ? $"[purple]{Markup.Escape(project.Name)}[/] is now internal: every logged-in user can read it."
                : $"[purple]{Markup.Escape(project.Name)}[/] is now private: only users and groups with a grant can see it.");

            return 0;
        }
    }
}
