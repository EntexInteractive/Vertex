// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Auth;
using Vertex.Cli.Net;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Requests;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Users
{
    public class UserNameSettings : NetworkSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("The user's username.")]
        public string Name { get; set; } = string.Empty;
    }

    public sealed class AddUserSettings : UserNameSettings
    {
        [CommandOption("--admin")]
        [Description("Makes the user a server admin.")]
        public bool Admin { get; set; }

        [CommandOption("--display-name")]
        [Description("The name shown to other people. Defaults to the username.")]
        public string? DisplayName { get; set; }

        [CommandOption("--email")]
        [Description("The user's email address.")]
        public string? Email { get; set; }

        [CommandOption("--password-stdin")]
        [Description("Reads the initial password from standard input instead of prompting.")]
        public bool PasswordStdin { get; set; }
    }

    public sealed class UserAdminSettings : UserNameSettings
    {
        [CommandOption("--remove")]
        [Description("Removes server admin instead of granting it.")]
        public bool Remove { get; set; }
    }

    /// <summary>
    /// <c>vx user add &lt;name&gt;</c>: creates a user with an initial password. Server admins only.
    /// </summary>
    public sealed class AddUserCommand : AsyncCommand<AddUserSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, AddUserSettings settings, CancellationToken cancellationToken)
        {
            string? password = await PasswordPrompt.ReadAsync($"Initial password for {settings.Name}:", settings.PasswordStdin, $"vx user add {settings.Name} --password-stdin", true, cancellationToken);
            if (password is null) return 1;

            using UsersInterface users = new(settings.ServerAddress, settings.Token);
            ServerResult<VUser> result = await users.CreateUserAsync(new CreateUserRequest(settings.Name, password, settings.Admin, settings.DisplayName, settings.Email), cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not add the user", result);

            string admin = result.Data!.IsAdmin ? " as a server admin" : "";
            AnsiConsole.MarkupLine($"Added user [purple]{Markup.Escape(result.Data.Username)}[/]{admin}. They can log in with 'vx login -u {Markup.Escape(result.Data.Username)}'.");
            return 0;
        }
    }

    /// <summary>
    /// <c>vx user list</c>: lists all users. Server admins only.
    /// </summary>
    public sealed class ListUsersCommand : AsyncCommand<NetworkSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, NetworkSettings settings, CancellationToken cancellationToken)
        {
            using UsersInterface users = new(settings.ServerAddress, settings.Token);
            ServerResult<IReadOnlyList<VUser>> result = await users.ListUsersAsync(cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not list users", result);

            Table table = new Table().Border(TableBorder.Simple).AddColumns("User", "Name", "Email", "Role", "Last login");
            foreach (VUser user in result.Data!)
            {
                string role = user.Disabled ? "[red]disabled[/]" : user.IsAdmin ? "[purple]admin[/]" : "user";
                string lastLogin = user.LastLoginAt is { } last ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "[grey]never[/]";
                table.AddRow(Markup.Escape(user.Username), Markup.Escape(user.DisplayName), Markup.Escape(user.Email), role, lastLogin);
            }

            AnsiConsole.Write(table);
            return 0;
        }
    }

    /// <summary>
    /// <c>vx user disable &lt;name&gt;</c> and <c>vx user enable &lt;name&gt;</c>. A disabled user can't log in, and their
    /// existing logins stop working on their next command. Server admins only.
    /// </summary>
    public abstract class SetUserDisabledCommand : AsyncCommand<UserNameSettings>
    {
        protected abstract bool Disable { get; }

        public override async Task<int> ExecuteAsync(CommandContext context, UserNameSettings settings, CancellationToken cancellationToken)
        {
            using UsersInterface users = new(settings.ServerAddress, settings.Token);
            ServerResult<VUser> result = await users.UpdateUserAsync(settings.Name, new UpdateUserRequest(Disabled: Disable), cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print(Disable ? "Could not disable the user" : "Could not enable the user", result);

            AnsiConsole.MarkupLine($"{(Disable ? "Disabled" : "Enabled")} [purple]{Markup.Escape(result.Data!.Username)}[/].");
            return 0;
        }
    }

    public sealed class DisableUserCommand : SetUserDisabledCommand
    {
        protected override bool Disable => true;
    }

    public sealed class EnableUserCommand : SetUserDisabledCommand
    {
        protected override bool Disable => false;
    }

    /// <summary>
    /// <c>vx user admin &lt;name&gt;</c>: makes a user a server admin, or removes it with <c>--remove</c>. Server admins
    /// only. The server refuses to remove the last admin.
    /// </summary>
    public sealed class UserAdminCommand : AsyncCommand<UserAdminSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, UserAdminSettings settings, CancellationToken cancellationToken)
        {
            using UsersInterface users = new(settings.ServerAddress, settings.Token);
            ServerResult<VUser> result = await users.UpdateUserAsync(settings.Name, new UpdateUserRequest(IsAdmin: !settings.Remove), cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not change the user", result);

            AnsiConsole.MarkupLine(settings.Remove
                ? $"[purple]{Markup.Escape(result.Data!.Username)}[/] is no longer a server admin."
                : $"[purple]{Markup.Escape(result.Data!.Username)}[/] is now a server admin.");

            return 0;
        }
    }

    /// <summary>
    /// <c>vx passwd</c>: changes your own password and logs out your other devices.
    /// </summary>
    public sealed class PasswdCommand : AsyncCommand<NetworkSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, NetworkSettings settings, CancellationToken cancellationToken)
        {
            if (settings.Credential is null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ServerErrors.NotLoggedIn(settings.ServerAddress))}[/]");
                return 1;
            }

            string? current = await PasswordPrompt.ReadAsync("Current password:", false, "vx passwd", false, cancellationToken);
            if (current is null) return 1;

            string? next = await PasswordPrompt.ReadAsync("New password:", false, "vx passwd", true, cancellationToken);
            if (next is null) return 1;

            using UsersInterface users = new(settings.ServerAddress, settings.Token);
            ServerResult<bool> result = await users.ChangePasswordAsync(new ChangePasswordRequest(current, next), cancellationToken);
            if (!result.IsSuccessStatusCode) return ServerErrors.Print("Could not change the password", result);

            AnsiConsole.MarkupLine("Changed your password. Your other devices are logged out.");
            return 0;
        }
    }
}
