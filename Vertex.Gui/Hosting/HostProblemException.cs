// Copyright 2026 Entex Interactive

namespace Vertex.Gui.Hosting
{
    /// <summary>
    /// A failure to show the user as is. <see cref="ServerErrorHandler"/> turns it into a ProblemDetails response whose
    /// <c>detail</c> is <see cref="Exception.Message"/>, so phrase the message for people and name the fixing command.
    /// </summary>
    public sealed class HostProblemException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }
}
