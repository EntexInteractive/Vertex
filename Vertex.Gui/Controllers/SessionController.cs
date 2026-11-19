// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Client.Auth;
using Vertex.Core.Client.Config;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Requests;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;
using Vertex.Gui.Hosting;

namespace Vertex.Gui.Controllers
{
    /// <summary>
    /// The server the desktop app talks to and who is logged in there.
    /// </summary>
    /// <param name="Server">The server address, from <c>vx config server</c> or the built-in default.</param>
    /// <param name="ServerSource">Where <paramref name="Server"/> came from: <c>vx config</c> or <c>built-in default</c>.</param>
    /// <param name="Username">The logged-in user, or null when not logged in.</param>
    /// <param name="LoggedIn">True when a login for <paramref name="Server"/> is saved and readable.</param>
    /// <param name="LoginCommand">The CLI command that logs in, for hints.</param>
    public sealed record SessionDto(string Server, string ServerSource, string? Username, bool LoggedIn, string LoginCommand);

    /// <param name="Server">The server to log in to. Becomes the default server for the app and for <c>vx</c>, like
    /// <c>vx config server</c>. Null keeps the current one.</param>
    public sealed record LoginRequestDto(string? Server, string Username, string Password);

    [ApiController]
    [Route("api/session")]
    public sealed class SessionController(ServerSession session) : ControllerBase
    {
        /// <summary>
        /// Returns the current server and login. Never returns the token.
        /// </summary>
        [HttpGet]
        public SessionDto Get()
        {
            return Describe();
        }

        /// <summary>
        /// Logs in like <c>vx login</c> and saves the token where the CLI finds it, so both are logged in.
        /// </summary>
        [HttpPost("login")]
        public async Task<SessionDto> Login(LoginRequestDto request, CancellationToken cancellationToken)
        {
            string username = request.Username.Trim();
            if (username.Length == 0 || request.Password.Length == 0) throw new HostProblemException("Enter a username and password.");

            string server = string.IsNullOrWhiteSpace(request.Server) ? session.Server : request.Server.Trim().TrimEnd('/');
            if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
                throw new HostProblemException($"'{server}' isn't a server address. Use one like https://vertex.example.com.");

            Credential? previous = CredentialStore.Get(server);
            using AuthInterface auth = new(server);
            ServerResult<LoginResponse> result = await auth.LoginAsync(new LoginRequest(username, request.Password, Environment.MachineName), cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                if (result.StatusCode == 0) throw new HostProblemException($"Can't reach the server at {server}. Check the address.", StatusCodes.Status502BadGateway);
                string message = string.IsNullOrWhiteSpace(result.Exception?.Message) ? $"The server returned {result.StatusCode}." : result.Exception.Message;
                throw new HostProblemException(message, result.StatusCode == 401 ? StatusCodes.Status401Unauthorized : StatusCodes.Status400BadRequest);
            }

            LoginResponse login = result.Data!;
            CredentialStore.Save(server, new Credential(login.User.Username, login.Token));

            if (CredentialStore.NormalizeServer(server) != CredentialStore.NormalizeServer(session.Server))
            {
                UserConfig config = UserConfig.Load();
                config.Server = server;
                config.Save();
            }

            // The new token replaces the old one, so revoke the old one rather than leave it valid until it expires.
            if (previous is not null) await RevokeQuietlyAsync(server, previous.Token, cancellationToken);
            return Describe();
        }

        /// <summary>
        /// Logs out like <c>vx logout</c>: revokes the token on the server and forgets it here.
        /// </summary>
        [HttpPost("logout")]
        public async Task<SessionDto> Logout(CancellationToken cancellationToken)
        {
            string server = session.Server;
            Credential? credential = CredentialStore.Get(server);
            if (credential is not null)
            {
                await RevokeQuietlyAsync(server, credential.Token, cancellationToken);
                CredentialStore.Remove(server);
            }

            return Describe();
        }

        private SessionDto Describe()
        {
            string? configured = session.ConfiguredServer;
            Credential? credential = session.Credential;
            return new SessionDto(session.Server, configured is null ? "built-in default" : "vx config", credential?.Username, credential is not null, session.LoginCommand);
        }

        private static async Task RevokeQuietlyAsync(string server, string token, CancellationToken cancellationToken)
        {
            try
            {
                using AuthInterface auth = new(server, token);
                await auth.LogoutAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A token that's already gone or a server that's down shouldn't stop logging in or out.
            }
        }
    }
}
