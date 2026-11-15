// Copyright 2026 Entex Interactive

using Grpc.Core;
using Vertex.Core.Net;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Net
{
    /// <summary>
    /// Turns server failures into one-line messages that name the fix.
    /// </summary>
    public static class ServerErrors
    {
        /// <summary>
        /// Describes errors that every command can hit the same way, such as not being logged in.
        /// </summary>
        /// <returns>The message, or null when the exception isn't one of them.</returns>
        public static string? Describe(Exception exception)
        {
            switch (exception)
            {
                case NotLoggedInException notLoggedIn:
                    return NotLoggedIn(notLoggedIn.Server);
                case RpcException { StatusCode: StatusCode.Unauthenticated }:
                    return NotLoggedIn(NetworkSettings.Current?.ServerAddress ?? NetworkSettings.DefaultServerAddress);
                case RpcException { StatusCode: StatusCode.PermissionDenied } denied:
                    return string.IsNullOrWhiteSpace(denied.Status.Detail) ? "You don't have access to do that." : denied.Status.Detail;
                case RpcException { StatusCode: StatusCode.Unimplemented }:
                    return $"The server at {NetworkSettings.Current?.ServerAddress ?? NetworkSettings.DefaultServerAddress} is older than this vx and can't do that yet. Update the server.";
                case RpcException { StatusCode: StatusCode.FailedPrecondition or StatusCode.InvalidArgument or StatusCode.NotFound or StatusCode.AlreadyExists } rpc
                    when !string.IsNullOrWhiteSpace(rpc.Status.Detail):
                    // The server phrases these for the user, naming the command that fixes them.
                    return rpc.Status.Detail;
                case RpcException { StatusCode: StatusCode.Unknown or StatusCode.Internal }:
                    return "The server failed to handle the request. Its log has the details; run the command again later or tell the server admin.";
                case RpcException { StatusCode: StatusCode.Unavailable }:
                    return $"Can't reach the server at {NetworkSettings.Current?.ServerAddress ?? NetworkSettings.DefaultServerAddress}. Check the address with 'vx info'.";
                default:
                    return null;
            }
        }

        public static string NotLoggedIn(string server)
        {
            return $"Not logged in to {server}. Run '{NetworkSettings.LoginCommandFor(server)}'.";
        }

        /// <summary>
        /// The server's message for a failed call, e.g. "You need admin access to 'Game'. Ask a project admin...".
        /// </summary>
        public static string Message<T>(ServerResult<T> result)
        {
            string? message = result.Exception?.Message;
            return string.IsNullOrWhiteSpace(message) ? $"The server returned {result.StatusCode}." : message;
        }

        /// <summary>
        /// Prints a failed call as one red line, prefixed with what was being done.
        /// </summary>
        /// <returns>1, the exit code for a failure.</returns>
        public static int Print<T>(string action, ServerResult<T> result)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(action)}: {Markup.Escape(Message(result))}[/]");
            return 1;
        }
    }
}
