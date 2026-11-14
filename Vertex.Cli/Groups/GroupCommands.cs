// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Groups
{
    public class GroupNameSettings : NetworkSettings
    {
        [CommandArgument(0, "<group>")]
        [Description("The group's name.")]
        public string Name { get; set; } = string.Empty;
    }

    public sealed class DeleteGroupSettings : GroupNameSettings
    {
        [CommandOption("--force|-f")]
        [Description("Deletes without asking for confirmation.")]
        public bool Force { get; set; }
    }

    public sealed class ListGroupsSettings : NetworkSettings
    {
        [CommandArgument(0, "[group]")]
        [Description("A group to list the members of. Omit it to list all groups.")]
        public string? Name { get; set; }
    }

    public sealed class GroupMembersSettings : GroupNameSettings
    {
        [CommandArgument(1, "<users>")]
        [Description("One or more usernames.")]
        public string[] Users { get; set; } = [];
    }

    /// <summary>
    /// <c>vx group create &lt;group&gt;</c>. Server admins only.
    /// </summary>
    public sealed class CreateGroupCommand : AsyncCommand<GroupNameSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, GroupNameSettings settings, CancellationToken cancellationToken)
        {
            using GroupsInterface groups = new(settings.ServerAddress, settings.Token);
            ServerResult<VGroup> result = await groups.CreateGroupAsync(settings.Name, cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not create the group", result);

            string name = Markup.Escape(result.Data!.Name);
            AnsiConsole.MarkupLine($"Created group [purple]{name}[/]. Add people with 'vx group add {name} <users...>'.");
            return 0;
        }
    }

    /// <summary>
    /// <c>vx group delete &lt;group&gt;</c>: deletes a group and its project grants. Server admins only.
    /// </summary>
    public sealed class DeleteGroupCommand : AsyncCommand<DeleteGroupSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, DeleteGroupSettings settings, CancellationToken cancellationToken)
        {
            // Members lose every grant the group gave them, so ask unless told not to.
            if (!settings.Force)
            {
                if (Console.IsInputRedirected)
                {
                    AnsiConsole.MarkupLine("[red]Deleting a group needs confirmation. Use --force to skip it.[/]");
                    return 1;
                }

                if (!await AnsiConsole.ConfirmAsync($"Delete group [purple]{Markup.Escape(settings.Name)}[/]? Its members lose the access it gave them.", false, cancellationToken))
                    return 1;
            }

            using GroupsInterface groups = new(settings.ServerAddress, settings.Token);
            ServerResult<bool> result = await groups.DeleteGroupAsync(settings.Name, cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not delete the group", result);

            AnsiConsole.MarkupLine($"Deleted group [purple]{Markup.Escape(settings.Name)}[/].");
            return 0;
        }
    }

    /// <summary>
    /// <c>vx group list</c> lists groups; <c>vx group list &lt;group&gt;</c> lists a group's members.
    /// </summary>
    public sealed class ListGroupsCommand : AsyncCommand<ListGroupsSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, ListGroupsSettings settings, CancellationToken cancellationToken)
        {
            using GroupsInterface groups = new(settings.ServerAddress, settings.Token);
            if (settings.Name is not null)
            {
                ServerResult<IReadOnlyList<VUser>> members = await groups.ListMembersAsync(settings.Name, cancellationToken);
                if (!members.IsSuccessStatusCode) return ServerErrors.Print("Could not list the members", members);

                foreach (VUser user in members.Data!)
                {
                    string name = user.DisplayName == user.Username ? "" : $" [grey]({Markup.Escape(user.DisplayName)})[/]";
                    AnsiConsole.MarkupLine($"  {Markup.Escape(user.Username)}{name}");
                }

                if (members.Data.Count == 0) AnsiConsole.MarkupLine($"[grey]No members. Add some with 'vx group add {Markup.Escape(settings.Name)} <users...>'.[/]");
                return 0;
            }

            ServerResult<IReadOnlyList<VGroup>> result = await groups.ListGroupsAsync(cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not list groups", result);

            foreach (VGroup group in result.Data!)
                AnsiConsole.MarkupLine($"  {Markup.Escape(group.Name)} [grey]({group.MemberCount:N0} {(group.MemberCount == 1 ? "member" : "members")})[/]");

            if (result.Data.Count == 0) AnsiConsole.MarkupLine("[grey]No groups. Create one with 'vx group create <group>'.[/]");
            return 0;
        }
    }

    /// <summary>
    /// <c>vx group add &lt;group&gt; &lt;users...&gt;</c>. Users already in the group are skipped. Server admins only.
    /// </summary>
    public sealed class AddGroupMembersCommand : AsyncCommand<GroupMembersSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, GroupMembersSettings settings, CancellationToken cancellationToken)
        {
            using GroupsInterface groups = new(settings.ServerAddress, settings.Token);
            ServerResult<int> result = await groups.AddMembersAsync(settings.Name, settings.Users, cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not add the members", result);

            int skipped = settings.Users.Distinct(StringComparer.OrdinalIgnoreCase).Count() - result.Data;
            string already = skipped > 0 ? $" [grey]({skipped:N0} already in it)[/]" : "";
            AnsiConsole.MarkupLine($"Added {result.Data:N0} {(result.Data == 1 ? "user" : "users")} to [purple]{Markup.Escape(settings.Name)}[/].{already}");
            return 0;
        }
    }

    /// <summary>
    /// <c>vx group remove &lt;group&gt; &lt;users...&gt;</c>. Users not in the group are skipped. Server admins only.
    /// </summary>
    public sealed class RemoveGroupMembersCommand : AsyncCommand<GroupMembersSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, GroupMembersSettings settings, CancellationToken cancellationToken)
        {
            using GroupsInterface groups = new(settings.ServerAddress, settings.Token);
            ServerResult<int> result = await groups.RemoveMembersAsync(settings.Name, settings.Users, cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not remove the members", result);

            AnsiConsole.MarkupLine($"Removed {result.Data:N0} {(result.Data == 1 ? "user" : "users")} from [purple]{Markup.Escape(settings.Name)}[/].");
            return 0;
        }
    }
}
