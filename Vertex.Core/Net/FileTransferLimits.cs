// Copyright 2026 Entex Interactive

namespace Vertex.Core.Net
{
    /// <summary>
    /// Shared sizing for streamed gRPC file transfers so the client and server agree on limits.
    /// </summary>
    public static class FileTransferLimits
    {
        /// <summary>
        /// Size of each streamed data chunk. Large enough to keep per-message overhead negligible,
        /// small enough to keep memory per transfer bounded.
        /// </summary>
        public const int ChunkSize = 1024 * 1024;

        /// <summary>
        /// Largest chunk either side accepts. Leaves headroom so the chunk size can be raised later
        /// without breaking older peers.
        /// </summary>
        public const int MaximumChunkSize = 4 * 1024 * 1024;

        /// <summary>
        /// gRPC message limit: one maximum chunk plus protobuf framing.
        /// </summary>
        public const int MaximumMessageSize = MaximumChunkSize + 64 * 1024;

        /// <summary>
        /// HTTP/2 flow-control windows. Throughput on a single stream is capped at roughly
        /// window / round-trip time, so the defaults (64 KB-768 KB) starve high-latency links.
        /// </summary>
        public const int StreamWindowSize = 16 * 1024 * 1024;

        public const int ConnectionWindowSize = 64 * 1024 * 1024;
    }
}