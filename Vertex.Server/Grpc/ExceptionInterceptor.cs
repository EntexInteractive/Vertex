// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Amazon.Runtime;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Npgsql;
using Vertex.Server.Telemetry;

namespace Vertex.Server.Grpc
{
    /// <summary>
    /// Turns exceptions that escape a gRPC service into a status the client can act on, logs the cause once, and counts
    /// it on <c>vertex.grpc.errors</c>. Without it every exception reaches the client as <c>Unknown</c>, and the cause is
    /// only in an unhandled-exception log entry.
    /// </summary>
    /// <remarks>
    /// Overload (database or storage too slow, connection pool exhausted, S3 throttling) becomes <c>Unavailable</c>:
    /// retrying later is the fix. A client that went away becomes <c>Cancelled</c> and is not logged as an error.
    /// Anything else is a server bug and becomes <c>Internal</c> naming the exception type.
    /// </remarks>
    public sealed class ExceptionInterceptor : Interceptor
    {
        private const string DatabaseBusy = "The database is busy; retry shortly.";
        private const string StorageBusy = "Storage is busy; retry shortly.";
        private const string ServerBusy = "The server is busy; retry shortly.";

        private readonly ILogger<ExceptionInterceptor> _logger;

        public ExceptionInterceptor(ILogger<ExceptionInterceptor> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                return await continuation(request, context);
            }
            catch (Exception exception) when (exception is not RpcException)
            {
                throw Translate(exception, context, start);
            }
        }

        /// <inheritdoc />
        public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
            ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                await continuation(request, responseStream, context);
            }
            catch (Exception exception) when (exception is not RpcException)
            {
                throw Translate(exception, context, start);
            }
        }

        private RpcException Translate(Exception exception, ServerCallContext context, long start)
        {
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            string type = exception.GetType().Name;

            if (exception is OperationCanceledException && context.CancellationToken.IsCancellationRequested)
            {
                Count(context, StatusCode.Cancelled, type);
                _logger.LogDebug("{Method} cancelled by the client after {Elapsed:F1} s", context.Method, elapsed);
                return new RpcException(new Status(StatusCode.Cancelled, "The call was cancelled."));
            }

            (StatusCode code, string message) = Classify(exception);
            Count(context, code, type);

            if (code == StatusCode.Unavailable)
                _logger.LogWarning(exception, "{Method} failed after {Elapsed:F1} s: {Message} ({ExceptionType}: {Detail})", context.Method, elapsed, message, type, exception.Message);
            else
                _logger.LogError(exception, "{Method} failed after {Elapsed:F1} s with {ExceptionType}", context.Method, elapsed, type);

            return new RpcException(new Status(code, message, exception));
        }

        private static (StatusCode Code, string Message) Classify(Exception exception)
        {
            switch (exception)
            {
                // A stored chunk or manifest that fails its checks (an IOException, so before the storage cases).
                case InvalidDataException:
                    return (StatusCode.DataLoss, "Stored data failed its integrity check. Tell the server admin; the server log names the object.");

                // Pool exhausted ("The connection pool has been exhausted"), command timeouts, connection failures and
                // serialization conflicts are all transient.
                case NpgsqlException { IsTransient: true }:
                case NpgsqlException { InnerException: TimeoutException }:
                    return (StatusCode.Unavailable, DatabaseBusy);

                // S3 errors: 5xx, throttling (SlowDown), and failures to reach it at all.
                case AmazonServiceException service when (int)service.StatusCode >= 500 || service.ErrorCode is "SlowDown" or "RequestTimeout":
                case AmazonClientException:
                case HttpRequestException:
                case IOException:
                    return (StatusCode.Unavailable, StorageBusy);

                // A timeout of our own (S3 client, budget, database) while the client is still waiting.
                case TimeoutException:
                case OperationCanceledException:
                    return (StatusCode.Unavailable, ServerBusy);

                default:
                    return (StatusCode.Internal, $"The server failed to handle the request ({exception.GetType().Name}). Its log has the details.");
            }
        }

        private static void Count(ServerCallContext context, StatusCode code, string exceptionType)
        {
            VertexMetrics.GrpcErrors.Add(1, VertexMetrics.Tag("method", context.Method), VertexMetrics.Tag("status", code.ToString()),
                VertexMetrics.Tag("exception", exceptionType));
        }
    }
}
