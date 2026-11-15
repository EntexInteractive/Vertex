// Copyright 2026 Entex Interactive

using Vertex.Core.Storage;

namespace Vertex.Core.Net
{
    /// <summary>
    /// Shared sizing for chunked gRPC file transfers so the client and server agree on limits.
    /// </summary>
    public static class FileTransferLimits
    {
        /// <summary>
        /// gRPC message limit: one maximum-size chunk after zstd's worst-case expansion, plus protobuf framing.
        /// Manifests and id lists are batched to stay under it as well.
        /// </summary>
        public const int MaximumMessageSize = ContentChunker.MaximumChunkSize + 256 * 1024;

        /// <summary>
        /// Raw bytes packed into one UploadChunks request. Small chunks share a request; a maximum-size chunk goes
        /// alone. Compressed data plus framing then always fits in <see cref="MaximumMessageSize"/>, and each
        /// request stays far below proxy body limits (Cloudflare: 100 MB on Free/Pro).
        /// </summary>
        public const long MaximumUploadRequestBytes = ContentChunker.MaximumChunkSize;

        /// <summary>
        /// Raw bytes per DownloadChunks response stream, so one call can't run past proxy response timeouts.
        /// </summary>
        public const long MaximumDownloadStreamBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Most ids in one FindMissing request (64 bytes per hex file id, 32 per chunk id).
        /// </summary>
        public const int MaximumIdsPerRequest = 16_384;

        /// <summary>
        /// Most chunk refs across manifests in one SubmitFiles request (~40 bytes each).
        /// </summary>
        public const int MaximumChunksPerCommit = 50_000;

        /// <summary>
        /// Most chunk ids in one DownloadChunks request.
        /// </summary>
        public const int MaximumChunksPerDownload = 1_024;

        /// <summary>
        /// Largest Submit request (roughly 200 bytes per changed file), so a first submit of a whole game project fits
        /// in one call while staying under proxy body limits (Cloudflare: 100 MB).
        /// </summary>
        public const int MaximumSubmitMessageSize = 64 * 1024 * 1024;

        /// <summary>
        /// Most changed files in one Submit request.
        /// </summary>
        public const int MaximumChangesPerSubmit = 250_000;

        /// <summary>
        /// Files per GetHeadChanges page (a few MB at most).
        /// </summary>
        public const int HeadChangesPageSize = 10_000;

        /// <summary>
        /// Most paths in one Lock or Unlock request.
        /// </summary>
        public const int MaximumPathsPerLockRequest = 5_000;

        /// <summary>
        /// Most lock events one GetLockEvents call returns; a client further behind gets the full lock list instead.
        /// </summary>
        public const int MaximumLockEvents = 50_000;

        /// <summary>
        /// HTTP/2 flow-control windows. Throughput on a single stream is capped at roughly
        /// window / round-trip time, so the defaults (64 KB-768 KB) starve high-latency links.
        /// </summary>
        public const int StreamWindowSize = 16 * 1024 * 1024;

        public const int ConnectionWindowSize = 64 * 1024 * 1024;
    }
}
