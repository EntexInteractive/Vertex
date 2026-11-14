// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;
using Vertex.Core.Client.Auth;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Requests;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Auth
{
    public sealed class LoginSettings : NetworkSettings
    {
        [CommandArgument(0, "[server]")]
        [Description("The server to log in to. Defaults to the workspace's server.")]
        public string? Server { get; set; }

        [CommandOption("--username|-u")]
        [Description("The username. Prompted for when omitted.")]
        public string? Username { get; set; }

        [CommandOption("--password-stdin")]
        [Description("Reads the password from standard input, for scripts and CI.")]
        public bool PasswordStdin { get; set; }
    }

    /// <summary>
    /// Logs in to a server and saves the token for every later command, like <c>docker login</c>. Running it again
    /// replaces the saved login.
    /// </summary>
    public sealed class LoginCommand : AsyncCommand<LoginSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings, CancellationToken cancellationToken)
        {
            if (settings.Server is not null) settings.ServerAddress = settings.Server;
            string server = settings.ServerAddress;
            Credential? previous = settings.Credential;

            string? username = settings.Username?.Trim();
            if (string.IsNullOrEmpty(username))
            {
                if (settings.PasswordStdin || Console.IsInputRedirected)
                {
                    AnsiConsole.MarkupLine($"[red]Name the user with -u, e.g. 'vx login {Markup.Escape(server)} -u <user> --password-stdin'.[/]");
                    return 1;
                }

                TextPrompt<string> prompt = new("Username:");
                if (previous is not null) prompt.DefaultValue(previous.Username);
                username = (await AnsiConsole.PromptAsync(prompt, cancellationToken)).Trim();
            }

            string? password = await PasswordPrompt.ReadAsync("Password:", settings.PasswordStdin, $"vx login {server} -u {username} --password-stdin", false, cancellationToken);
            if (password is null) return 1;

            using AuthInterface auth = new(server);
            ServerResult<LoginResponse> result = await auth.LoginAsync(new LoginRequest(username, password, Environment.MachineName), cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                string reason = result.StatusCode == 0 ? $"Could not reach {server}: {ServerErrors.Message(result)}" : ServerErrors.Message(result);
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(reason)}[/]");
                return 1;
            }

            LoginResponse login = result.Data!;
            CredentialStore.Save(server, new Credential(login.User.Username, login.Token));

            // The new token replaces the old one, so revoke the old one rather than leave it valid until it expires.
            if (previous is not null) await RevokeQuietlyAsync(server, previous.Token, cancellationToken);

            AnsiConsole.MarkupLine($"Logged in to [purple]{Markup.Escape(server)}[/] as [purple]{Markup.Escape(login.User.Username)}[/].");
            return 0;
        }

        /// <summary>
        /// Revokes a token on the server, ignoring failures: a token that's already gone or a server that's down
        /// shouldn't stop logging in or out.
        /// </summary>
        public static async Task RevokeQuietlyAsync(string server, string token, CancellationToken cancellationToken)
        {
            try
            {
                using AuthInterface auth = new(server, token);
                await auth.LogoutAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(ex, "Could not revoke the token on {Server}", server);
            }
        }
    }
}
