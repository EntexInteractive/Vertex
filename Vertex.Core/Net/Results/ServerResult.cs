// Copyright 2026 Entex Interactive

namespace Vertex.Net.Results
{
    /// <summary>
    /// Represents either a successful API value or a structured API error.
    /// </summary>
    public sealed class ServerResult<T>
    {
        /// <summary>
        /// Gets a value indicating whether the result represents a successful server operation.
        /// </summary>
        /// <value>
        /// <see langword="true"/> when the status code is in the 2xx range; otherwise, <see langword="false"/>.
        /// </value>
        public bool IsSuccess => StatusCode is >= 200 and < 300;

        /// <summary>
        /// Gets the value returned by the server when the operation completes successfully.
        /// </summary>
        /// <value>
        /// The deserialized response payload, or <see langword="null"/> when no value was returned or the operation failed.
        /// </value>
        public T? Data { get; init; }

        /// <summary>
        /// Gets the exception associated with a failed server operation.
        /// </summary>
        /// <value>
        /// The exception describing the error when the operation fails; otherwise, <see langword="null"/>.
        /// </value>
        public Exception? Exception { get; init; }

        /// <summary>
        /// Gets the HTTP-style status code associated with the server result.
        /// </summary>
        /// <value>
        /// An integer that indicates the outcome of the operation, typically using standard response code semantics.
        /// </value>
        public int StatusCode { get; init; }

        private ServerResult(T? data, Exception? exception, int statusCode)
        {
            Data = data;
            Exception = exception;
            StatusCode = statusCode;
        }

        /// <summary>
        /// Creates a successful server result containing the specified value and HTTP status code.
        /// </summary>
        /// <param name="value">The value returned by the operation.</param>
        /// <param name="statusCode">The HTTP status code associated with the successful result. Defaults to 200.</param>
        /// <return>A <see cref="ServerResult{T}"/> representing a successful operation with the provided value.</return>
        public static ServerResult<T> Success(T value, int statusCode = 200)
        {
            return new ServerResult<T>(value, null, statusCode);
        }

        /// <summary>
        /// Creates a failed server result containing the specified exception and HTTP status code.
        /// </summary>
        /// <param name="exception">The exception describing the failure.</param>
        /// <param name="statusCode">The HTTP status code associated with the failure. Defaults to 500.</param>
        /// <return>A <see cref="ServerResult{T}"/> representing a failed operation with the provided exception.</return>
        public static ServerResult<T> Failure(Exception exception, int statusCode = 500)
        {
            return new ServerResult<T>(default, exception, statusCode);
        }
    }
}