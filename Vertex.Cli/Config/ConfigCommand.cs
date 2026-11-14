// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;
using Vertex.Core.Client.Config;

namespace Vertex.Cli.Config
{
    public sealed class ConfigSettings : NetworkSettings
    {
        [CommandArgument(0, "[key]")]
        [Description("The setting to show or change. Lists every setting when omitted.")]
        public string? Key { get; set; }

        [CommandArgument(1, "[value]")]
        [Description("The new value. Shows the current value when omitted.")]
        public string? Value { get; set; }

        [CommandOption("--unset")]
        [Description("Removes the saved value.")]
        public bool Unset { get; set; }
    }

    /// <summary>
    /// Shows and changes per-user settings that apply to every command on this machine, like Perforce's
    /// <c>p4 set</c>. Today the only setting is <c>server</c>, the default server address.
    /// </summary>
    public sealed class ConfigCommand : Command<ConfigSettings>
    {
        private const string ServerKey = "server";
        private static readonly string[] Keys = [ServerKey];

        public override int Execute(CommandContext context, ConfigSettings settings, CancellationToken cancellationToken)
        {
            if (settings.Key is null)
            {
                if (settings.Unset)
                {
                    AnsiConsole.MarkupLine("[red]Name the setting to remove, e.g. 'vx config server --unset'.[/]");
                    return 1;
                }

                PrintServer(settings);
                return 0;
            }

            if (!settings.Key.Equals(ServerKey, StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[red]Unknown setting '{Markup.Escape(settings.Key)}'. Available: {string.Join(", ", Keys)}.[/]");
                return 1;
            }

            if (settings.Unset && settings.Value is not null)
            {
                AnsiConsole.MarkupLine("[red]Give either a value or --unset, not both.[/]");
                return 1;
            }

            if (settings.Unset) return UnsetServer(settings);
            if (settings.Value is null)
            {
                PrintServer(settings);
                return 0;
            }

            return SetServer(settings, settings.Value);
        }

        private static int SetServer(ConfigSettings settings, string value)
        {
            string server = value.Trim().TrimEnd('/');
            if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                AnsiConsole.MarkupLine($"[red]'{Markup.Escape(value)}' isn't a server address. Use a full URL, e.g. 'vx config server https://vertex.example.com'.[/]");
                return 1;
            }

            UserConfig config = settings.UserConfig;
            config.Server = server;
            config.Save();

            AnsiConsole.MarkupLine($"Default server set to [purple]{Markup.Escape(server)}[/].");
            if (settings.WorkspaceServer is not null)
            {
                AnsiConsole.MarkupLine($"[yellow]This workspace still uses its own server, {Markup.Escape(settings.WorkspaceServer)}.[/]");
            }

            return 0;
        }

        private static int UnsetServer(ConfigSettings settings)
        {
            UserConfig config = settings.UserConfig;
            if (config.Server is null)
            {
                AnsiConsole.MarkupLine("No default server is set.");
                return 0;
            }

            config.Server = null;
            config.Save();
            AnsiConsole.MarkupLine($"Removed the default server. Commands outside a workspace now use {Markup.Escape(NetworkSettings.DefaultServerAddress)}.");
            return 0;
        }

        /// <summary>
        /// Shows the server commands here would use and where it came from, plus the saved value when something
        /// overrides it.
        /// </summary>
        private static void PrintServer(ConfigSettings settings)
        {
            AnsiConsole.MarkupLine($"server  [purple]{Markup.Escape(settings.ServerAddress)}[/]  ({Markup.Escape(settings.ServerSource)})");

            string? saved = settings.ConfiguredServer;
            if (saved is not null && settings.ServerSource != "vx config")
            {
                AnsiConsole.MarkupLine($"        [grey]saved: {Markup.Escape(saved)} (overridden here)[/]");
            }
        }
    }
}
