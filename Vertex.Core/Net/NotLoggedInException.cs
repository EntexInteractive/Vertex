// Copyright 2026 Entex Interactive

namespace Vertex.Core.Net
{
    /// <summary>
    /// The server rejected the call because there is no valid token: never logged in, logged out, revoked, expired,
    /// or the account was disabled.
    /// </summary>
    public sealed class NotLoggedInException(string server) : Exception($"Not logged in to {server}.")
    {
        /// <summary>
        /// The server address that rejected the call.
        /// </summary>
        public string Server { get; } = server;
    }
}
