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
        [Description("read (sync and view history), write (submit and lock) or manage (manage access and branches).")]
        public string Level { get; set; } = string.Empty;
    }

    public sealed class RevokeSettings : AccessSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("A user or group.")]
        public string Name { get; set; } = string.Empty;
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

            using AccessInterface access = new(settings.ServerAddress, settings.Token);
            ServerResult<IReadOnlyList<VAccessGrant>> result = await access.ListGrantsAsync(project.Id, cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not list access", result);

            AnsiConsole.MarkupLine($"[purple]{Markup.Escape(project.Name)}[/] [grey](only these users and groups, and server admins, can see it)[/]");
            foreach (VAccessGrant grant in result.Data!)
            {
                string kind = grant.Kind == PrincipalKind.Group ? " [grey](group)[/]" : "";
                AnsiConsole.MarkupLine($"  {grant.Level.ToString().ToLowerInvariant(),-6} {Markup.Escape(grant.Name)}{kind}");
            }

            if (result.Data.Count == 0) AnsiConsole.MarkupLine("[grey]  No grants. Add one with 'vx access grant <user|group> <read|write|manage>'.[/]");
            return 0;
        }
    }

    /// <summary>
    /// <c>vx access grant &lt;user|group&gt; &lt;level&gt;</c>: sets a level, replacing any previous one. Needs manage access.
    /// </summary>
    public sealed class GrantAccessCommand : AsyncCommand<GrantSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, GrantSettings settings, CancellationToken cancellationToken)
        {
            if (!Enum.TryParse(settings.Level, true, out AccessLevel level) || level == AccessLevel.None || !Enum.IsDefined(level))
            {
                AnsiConsole.MarkupLine($"[red]'{Markup.Escape(settings.Level)}' isn't a level. Use read, write or manage.[/]");
                return 1;
            }

            if (level == AccessLevel.Admin)
            {
                AnsiConsole.MarkupLine($"[red]Server admin isn't a project level. Use 'manage' for this project, or 'vx user admin {Markup.Escape(settings.Name)}' to make them a server admin.[/]");
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
    /// <c>vx access revoke &lt;user|group&gt;</c>. Needs manage access.
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
}
