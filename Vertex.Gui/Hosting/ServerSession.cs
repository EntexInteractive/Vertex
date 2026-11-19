// Copyright 2026 Entex Interactive

using Vertex.Core.Client.Auth;
using Vertex.Core.Client.Config;
using Vertex.Core.Net;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Gui.Hosting
{
    /// <summary>
    /// The Vertex server the desktop app talks to and the saved login for it. Both are read from the files
    /// <c>vx config</c> and <c>vx login</c> write, on every call, so the app and the CLI always agree.
    /// </summary>
    public sealed class ServerSession
    {
        /// <summary>
        /// Same default as the CLI's <c>NetworkSettings.DefaultServerAddress</c>.
        /// </summary>
        public const string DefaultServer = "http://127.0.0.1:5000";

        /// <summary>
        /// The server from <c>vx config server</c>, or <see cref="DefaultServer"/>.
        /// </summary>
        public string Server => ConfiguredServer ?? DefaultServer;

        /// <summary>
        /// The server saved with <c>vx config server</c>, or null.
        /// </summary>
        public string? ConfiguredServer
        {
            get
            {
                string? server = UserConfig.Load().Server;
                return string.IsNullOrWhiteSpace(server) ? null : server.Trim();
            }
        }

        /// <summary>
        /// The saved login for <see cref="Server"/>, or null.
        /// </summary>
        public Credential? Credential => CredentialStore.Get(Server);

        /// <summary>
        /// The command that logs in to <see cref="Server"/>. Plain <c>vx login</c> resolves the same server.
        /// </summary>
        public string LoginCommand => "vx login";

        /// <summary>
        /// Creates a server interface that authenticates as the saved login.
        /// </summary>
        /// <exception cref="NotLoggedInException">There is no saved login for <see cref="Server"/>.</exception>
        public T Connect<T>(Func<string, string?, T> create) where T : BaseInterface
        {
            string server = Server;
            Credential credential = CredentialStore.Get(server) ?? throw new NotLoggedInException(server);
            return create(server, credential.Token);
        }
    }
}
