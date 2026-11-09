// Copyright 2026 Entex Interactive

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vertex.Net.Responses
{
    /// <summary>
    /// Version information returned by GET /api/v1/server/info.
    /// </summary>
    public sealed class InfoResponse
    {
        public string? Address { get; set; }
        public int CpuUsage { get; set; }
        public DateTime Date { get; set; }
        public double ProcessMs { get; set; }
        public double RamUsage { get; set; }
        public TimeSpan Uptime { get; set; }
        public string? Version { get; set; }
    }
}