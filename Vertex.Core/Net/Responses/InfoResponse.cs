// Copyright 2026 Entex Interactive

namespace Vertex.Core.Net.Responses
{
    /// <summary>
    /// Version information returned by GET /api/v1/server/info.
    /// </summary>
    public sealed record InfoResponse
    {
        /// <summary>
        /// Gets or sets the server address.
        /// </summary>
        public string? Address { get; set; }

        /// <summary>
        /// Gets or sets the build timestamp in UTC.
        /// </summary>
        public string? BuildTimeUtc { get; set; }

        /// <summary>
        /// Gets or sets the current date reported by the server.
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// Gets or sets the server start time.
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// Gets or sets the process uptime.
        /// </summary>
        public TimeSpan Uptime { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the connection is encrypted.
        /// </summary>
        public bool Encrypted { get; set; }

        /// <summary>
        /// Gets or sets the application version.
        /// </summary>
        public string? Version { get; set; }

        /// <summary>
        /// Gets or sets the machine name.
        /// </summary>
        public string? MachineName { get; set; }

        /// <summary>
        /// Gets or sets the operating system name.
        /// </summary>
        public string? OperatingSystem { get; set; }

        /// <summary>
        /// Gets or sets the system architecture.
        /// </summary>
        public string? Architecture { get; set; }

        /// <summary>
        /// Gets or sets the target framework.
        /// </summary>
        public string? Framework { get; set; }

        /// <summary>
        /// Gets or sets the number of available processors.
        /// </summary>
        public int ProcessorCount { get; set; }

        /// <summary>
        /// Gets or sets the system uptime.
        /// </summary>
        public TimeSpan SystemUptime { get; set; }

        /// <summary>
        /// Gets or sets the process uptime.
        /// </summary>
        public TimeSpan ProcessUptime { get; set; }
    }
}