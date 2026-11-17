// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Spectre.Console.Cli;
using Vertex.Core.Client.Auth;
using Vertex.Core.Client.Config;
using Vertex.Core.Client.Workspaces;

namespace Vertex.Cli.Net
{
    public class NetworkSettings : CommandSettings
    {
        public const string DefaultServerAddress = "http://127.0.0.1:5000";

        private string? _serverAddress;
        private VWorkspace? _workspace;
        private bool _workspaceChecked;
        private UserConfig? _userConfig;
        private Credential? _credential;
        private bool _credentialChecked;

        /// <summary>
        /// The settings of the command that is running, so the error handler can name the server in its hints.
        /// </summary>
        public static NetworkSettings? Current { get; set; }

        /// <summary>
        /// The server to talk to, like Perforce's P4PORT: <c>--server</c> when given, otherwise the current
        /// workspace's server, otherwise the one set with <c>vx config server</c>, otherwise
        /// <see cref="DefaultServerAddress"/>.
        /// </summary>
        [CommandOption("--server|-s")]
        [Description("The server address to use. Defaults to the workspace's server, then the one set with 'vx config server', then http://127.0.0.1:5000.")]
        public string ServerAddress
        {
            get => _serverAddress ?? WorkspaceServer ?? ConfiguredServer ?? DefaultServerAddress;
            set => _serverAddress = value;
        }

        /// <summary>
        /// True when the server was named on the command line rather than taken from the workspace or config.
        /// </summary>
        public bool ServerFromOption => _serverAddress is not null;

        /// <summary>
        /// Where <see cref="ServerAddress"/> came from, for <c>vx config</c> and <c>vx info</c>.
        /// </summary>
        public string ServerSource
        {
            get
            {
                if (_serverAddress is not null) return "--server";
                if (WorkspaceServer is not null) return $"workspace {Workspace!.Root}";
                if (ConfiguredServer is not null) return "vx config";
                return "built-in default";
            }
        }

        /// <summary>
        /// The server saved with <c>vx config server</c>, or null.
        /// </summary>
        public string? ConfiguredServer => string.IsNullOrWhiteSpace(UserConfig.Server) ? null : UserConfig.Server;

        /// <summary>
        /// The per-user settings, read once per command.
        /// </summary>
        public UserConfig UserConfig => _userConfig ??= UserConfig.Load();

        [CommandOption("--verbose|-v")]
        [Description("Displays verbose output.")]
        public bool Verbose { get; set; }

        /// <summary>
        /// The saved login for <see cref="ServerAddress"/>, or null when not logged in.
        /// </summary>
        public Credential? Credential
        {
            get
            {
                if (_credentialChecked) return _credential;
                _credentialChecked = true;
                return _credential = CredentialStore.Get(ServerAddress);
            }
        }

        /// <summary>
        /// The token to send, or null to call the server anonymously.
        /// </summary>
        public string? Token => Credential?.Token;

        /// <summary>
        /// The command to suggest when the server says we aren't logged in. Plain <c>vx login</c> resolves the same
        /// server unless <c>--server</c> pointed somewhere else.
        /// </summary>
        public static string LoginCommandFor(string server)
        {
            NetworkSettings? current = Current;
            bool sameAsDefault = current is not null && current._serverAddress is null && CredentialStore.NormalizeServer(current.ServerAddress) == CredentialStore.NormalizeServer(server);
            return sameAsDefault ? "vx login" : $"vx login {server}";
        }

        /// <summary>
        /// The workspace containing the current folder, or null.
        /// </summary>
        private VWorkspace? Workspace
        {
            get
            {
                if (_workspaceChecked) return _workspace;
                _workspaceChecked = true;
                return _workspace = VWorkspace.Find(Directory.GetCurrentDirectory());
            }
        }

        /// <summary>
        /// The server the current workspace pins, or null when it follows the configured server.
        /// </summary>
        public string? WorkspaceServer => string.IsNullOrWhiteSpace(Workspace?.Config.Address) ? null : Workspace.Config.Address;
    }
}
