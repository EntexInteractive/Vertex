// Copyright 2026 Entex Interactive

namespace Vertex.Core.Net.Responses
{
    /// <summary>
    /// Version information returned by GET /api/v1/server/info.
    /// </summary>
    public sealed record InfoResponse
    {
        public string? Address { get; set; }
        public string? BuildTimeUtc { get; set; }
        public DateTime Date { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public string? Version { get; set; }
        public string? MachineName { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Architecture { get; set; }
        public string? Framework { get; set; }
        public int ProcessorCount { get; set; }
        public TimeSpan SystemUptime { get; set; }
        public TimeSpan ProcessUptime { get; set; }
    }
}