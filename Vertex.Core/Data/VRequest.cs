// Copyright 2026 Entex Interactive

namespace Vertex.Core.Data
{
    public class VRequest
    {
        /// <summary>
        /// The time the request was handled.
        /// </summary>
        public DateTime ReceivedAt { get; } = DateTime.UtcNow;

        /// <summary>
        /// The unique user id that sent the request.
        /// </summary>
        public string? Uuid { get; }

        /// <summary>
        /// The HTTP method used in the request.
        /// </summary>
        public string Method { get; }

        /// <summary>
        /// The controller route of the request.
        /// </summary>
        public string Endpoint { get; }

        /// <summary>
        /// The response status code.
        /// </summary>
        public int StatusCode { get; }

        /// <summary>
        /// The total time, in milliseconds, it took to process the request.
        /// </summary>
        public long ProcessMs { get; }

        /// <summary>
        /// The total length, in bytes, of the request and response.
        /// </summary>
        public long Length { get; }

        public int CpuUsage { get; }
        public long RamUsage { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="VRequest"/> class.
        /// </summary>
        public VRequest(string? uuid, string method, string endpoint, int statusCode, long processMs, long length, int cpuUsage, long ramUsage)
        {
            Uuid = uuid;
            Method = method;
            Endpoint = endpoint;
            StatusCode = statusCode;
            ProcessMs = processMs;
            Length = length;
            CpuUsage = cpuUsage;
            RamUsage = ramUsage;
        }
    }
}