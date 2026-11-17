// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;
using Vertex.Core.Client.Auth;

namespace Vertex.Cli.Auth
{
    public sealed class LogoutSettings : NetworkSettings
    {
        [CommandArgument(0, "[server]")]
        [Description("The server to log out of. Defaults to the workspace's server, or http://127.0.0.1:5000.")]
        public string? Server { get; set; }
    }

    /// <summary>
    /// Revokes the saved token on the server and forgets it locally. Safe to re-run.
    /// </summary>
    public sealed class LogoutCommand : AsyncCommand<LogoutSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, LogoutSettings settings, CancellationToken cancellationToken)
        {
            if (settings.Server is not null) settings.ServerAddress = settings.Server;
            string server = settings.ServerAddress;

            Credential? credential = settings.Credential;
            if (credential is null)
            {
                AnsiConsole.MarkupLine($"[grey]Not logged in to {Markup.Escape(server)}.[/]");
                return 0;
            }

            await LoginCommand.RevokeQuietlyAsync(server, credential.Token, cancellationToken);
            CredentialStore.Remove(server);
            AnsiConsole.MarkupLine($"Logged out of [purple]{Markup.Escape(server)}[/].");
            return 0;
        }
    }
}
