// Copyright 2026 Entex Interactive

using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Net;

namespace Vertex.Gui.Hosting
{
    /// <summary>
    /// Turns exceptions from controllers into ProblemDetails whose <c>detail</c> the UI shows as is: one line that says
    /// what went wrong and what fixes it, the same wording the CLI uses (see <c>Vertex.Cli/Net/ServerErrors.cs</c>).
    /// </summary>
    public sealed class ServerErrorHandler(ServerSession session, IProblemDetailsService problems, ILogger<ServerErrorHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            (int status, string detail) = Describe(exception);
            if (status >= 500) logger.LogError(exception, "{Method} {Path} failed", context.Request.Method, context.Request.Path);

            context.Response.StatusCode = status;
            return await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                Exception = exception,
                ProblemDetails = new ProblemDetails { Status = status, Detail = detail }
            });
        }

        private (int Status, string Detail) Describe(Exception exception)
        {
            string server = session.Server;
            string notLoggedIn = $"Not logged in to {server}. Log in, or run '{session.LoginCommand}'.";
            switch (exception)
            {
                case HostProblemException problem:
                    return (problem.StatusCode, problem.Message);
                case NotLoggedInException:
                case RpcException { StatusCode: StatusCode.Unauthenticated }:
                    return (StatusCodes.Status401Unauthorized, notLoggedIn);
                case RpcException { StatusCode: StatusCode.PermissionDenied } denied:
                    return (StatusCodes.Status403Forbidden, string.IsNullOrWhiteSpace(denied.Status.Detail) ? "You don't have access to do that." : denied.Status.Detail);
                case RpcException { StatusCode: StatusCode.Unimplemented }:
                    return (StatusCodes.Status501NotImplemented, $"The server at {server} is older than this app and can't do that yet. Update the server.");
                case RpcException { StatusCode: StatusCode.FailedPrecondition or StatusCode.InvalidArgument or StatusCode.NotFound or StatusCode.AlreadyExists } rpc
                    when !string.IsNullOrWhiteSpace(rpc.Status.Detail):
                    return (StatusCodes.Status409Conflict, rpc.Status.Detail);
                case RpcException { StatusCode: StatusCode.Unavailable } rpc when rpc.Status.DebugException is null && !string.IsNullOrWhiteSpace(rpc.Status.Detail):
                    return (StatusCodes.Status503ServiceUnavailable, $"{rpc.Status.Detail} Try again.");
                case RpcException { StatusCode: StatusCode.Unavailable }:
                case HttpRequestException:
                    return (StatusCodes.Status502BadGateway, $"Can't reach the server at {server}. Check the address, or run 'vx info'.");
                case OperationCanceledException:
                    return (499, "The request was canceled.");
                default:
                    return (StatusCodes.Status500InternalServerError, "Something went wrong in the desktop app. Its log has the details.");
            }
        }
    }
}
