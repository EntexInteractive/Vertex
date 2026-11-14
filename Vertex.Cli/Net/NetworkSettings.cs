// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Spectre.Console.Cli;
using Vertex.Core.Client.Auth;
using Vertex.Core.Client.Workspaces;

namespace Vertex.Cli.Net
{
    public class NetworkSettings : CommandSettings
    {
        public const string DefaultServerAddress = "http://127.0.0.1:5000";

        private string? _serverAddress;
        private string? _workspaceServer;
        private bool _workspaceChecked;
        private Credential? _credential;
        private bool _credentialChecked;

        /// <summary>
        /// The settings of the command that is running, so the error handler can name the server in its hints.
        /// </summary>
        public static NetworkSettings? Current { get; set; }

        /// <summary>
        /// The server to talk to: <c>--server</c> when given, otherwise the current workspace's server, otherwise
        /// <see cref="DefaultServerAddress"/>.
        /// </summary>
        [CommandOption("--server|-s")]
        [Description("The server address to use. Defaults to the workspace's server, or http://127.0.0.1:5000.")]
        public string ServerAddress
        {
            get => _serverAddress ?? WorkspaceServer ?? DefaultServerAddress;
            set => _serverAddress = value;
        }

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

        private string? WorkspaceServer
        {
            get
            {
                if (_workspaceChecked) return _workspaceServer;
                _workspaceChecked = true;
                return _workspaceServer = VWorkspace.Find(Directory.GetCurrentDirectory())?.Config.Address;
            }
        }
    }
}
