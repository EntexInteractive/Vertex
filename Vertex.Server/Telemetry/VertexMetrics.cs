// Copyright 2026 Entex Interactive

using System.Diagnostics.Metrics;

namespace Vertex.Server.Telemetry
{
    /// <summary>
    /// Vertex's own metrics, next to the built-in ASP.NET Core, runtime and Npgsql ones. Exported for Prometheus when
    /// <c>VX_METRICS_PORT</c> is set; recording costs next to nothing when it isn't.
    /// </summary>
    /// <remarks>Prometheus names: dots become underscores and the unit is appended, e.g. <c>vertex_transfer_received_bytes_total</c>.</remarks>
    public static class VertexMetrics
    {
        public const string MeterName = "Vertex.Server";

        private static readonly Meter Meter = new(MeterName);

        /// <summary>Revisions submitted.</summary>
        public static readonly Counter<long> Revisions = Meter.CreateCounter<long>("vertex.revisions", "{revision}", "Revisions submitted.");

        /// <summary>Files changed by submitted revisions.</summary>
        public static readonly Counter<long> RevisionFiles = Meter.CreateCounter<long>("vertex.revisions.files", "{file}", "Files changed by submitted revisions.");

        /// <summary>Files locked.</summary>
        public static readonly Counter<long> LocksGranted = Meter.CreateCounter<long>("vertex.locks.granted", "{file}", "Files locked.");

        /// <summary>Locks released, tagged with <c>reason</c> (submitted, reverted, unlocked).</summary>
        public static readonly Counter<long> LocksReleased = Meter.CreateCounter<long>("vertex.locks.released", "{file}", "Locks released.");

        /// <summary>Submits refused because a file changed on the server or is locked by someone else, tagged with <c>reason</c>.</summary>
        public static readonly Counter<long> SubmitsRefused = Meter.CreateCounter<long>("vertex.revisions.refused", "{submit}", "Submits refused (out of date or locked).");

        /// <summary>Clients following a project's event stream on this instance.</summary>
        public static readonly UpDownCounter<long> EventStreams = Meter.CreateUpDownCounter<long>("vertex.event_streams", "{stream}", "Open event streams.");

        /// <summary>Compressed chunk bytes received from clients.</summary>
        public static readonly Counter<long> BytesReceived = Meter.CreateCounter<long>("vertex.transfer.received", "By", "Compressed chunk bytes uploaded by clients.");

        /// <summary>Compressed chunk bytes sent to clients.</summary>
        public static readonly Counter<long> BytesSent = Meter.CreateCounter<long>("vertex.transfer.sent", "By", "Compressed chunk bytes downloaded by clients.");
    }
}
