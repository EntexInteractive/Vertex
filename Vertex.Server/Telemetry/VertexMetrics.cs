// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Vertex.Server.Telemetry
{
    /// <summary>
    /// Vertex's own metrics, next to the built-in ASP.NET Core, runtime and Npgsql ones. Exported for Prometheus when
    /// <c>VX_METRICS_PORT</c> is set; recording costs next to nothing when it isn't.
    /// </summary>
    /// <remarks>
    /// Prometheus names: dots become underscores, the unit is appended and counters end in <c>_total</c>, e.g.
    /// <c>vertex.transfer.received</c> in bytes becomes <c>vertex_transfer_received_bytes_total</c>. Durations are in
    /// seconds, as OpenTelemetry recommends.
    /// </remarks>
    public static class VertexMetrics
    {
        public const string MeterName = "Vertex.Server";

        private static readonly Meter Meter = new(MeterName);

        /// <summary>Bucket boundaries for durations: 1 ms to 30 s.</summary>
        private static readonly InstrumentAdvice<double> Seconds = new()
        {
            HistogramBucketBoundaries = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30]
        };

        /// <summary>Bucket boundaries for files per revision.</summary>
        private static readonly InstrumentAdvice<long> FileCounts = new()
        {
            HistogramBucketBoundaries = [1, 2, 5, 10, 25, 50, 100, 250, 1_000, 10_000, 100_000]
        };

        /// <summary>Bucket boundaries for cached value sizes: 64 B to 1 MB.</summary>
        private static readonly InstrumentAdvice<long> Bytes = new()
        {
            HistogramBucketBoundaries = [64, 256, 1_024, 4_096, 16_384, 65_536, 262_144, 1_048_576]
        };

        /// <summary>Bucket boundaries for requests per shared transaction.</summary>
        private static readonly InstrumentAdvice<long> BatchSizes = new()
        {
            HistogramBucketBoundaries = [1, 2, 4, 8, 16, 32, 64]
        };

        // ---- Revisions and locks: what people do.

        /// <summary>Revisions submitted.</summary>
        public static readonly Counter<long> Revisions = Meter.CreateCounter<long>("vertex.revisions", "{revision}", "Revisions submitted.");

        /// <summary>Files changed per submitted revision.</summary>
        public static readonly Histogram<long> RevisionFiles = Meter.CreateHistogram("vertex.revisions.files", "{file}", "Files changed per submitted revision.", advice: FileCounts);

        /// <summary>Submits refused because a file changed on the server or is locked by someone else, tagged with <c>reason</c>.</summary>
        public static readonly Counter<long> SubmitsRefused = Meter.CreateCounter<long>("vertex.revisions.refused", "{submit}", "Submits refused (out of date or locked).");

        /// <summary>Files locked.</summary>
        public static readonly Counter<long> LocksGranted = Meter.CreateCounter<long>("vertex.locks.granted", "{file}", "Files locked.");

        /// <summary>Lock requests for files someone else already holds.</summary>
        public static readonly Counter<long> LocksContended = Meter.CreateCounter<long>("vertex.locks.contended", "{file}", "Lock requests for files held by someone else.");

        /// <summary>Locks released, tagged with <c>reason</c> (submitted, reverted, unlocked).</summary>
        public static readonly Counter<long> LocksReleased = Meter.CreateCounter<long>("vertex.locks.released", "{file}", "Locks released.");

        /// <summary>Files handed to clients by sync (changed on the branch since their last sync).</summary>
        public static readonly Counter<long> SyncFiles = Meter.CreateCounter<long>("vertex.sync.files", "{file}", "Files returned to syncing clients.");

        /// <summary>
        /// Time spent waiting for a project's write lock, tagged with <c>operation</c>. Writes in one project run one at
        /// a time, so this climbing is the first sign a project is near its write limit.
        /// </summary>
        public static readonly Histogram<double> ProjectLockWait = Meter.CreateHistogram("vertex.project_lock.wait", "s", "Time waiting for a project's write lock.", advice: Seconds);

        /// <summary>
        /// Time a project's write lock is held, from taking it to the end of the commit, tagged with <c>operation</c>
        /// (<c>locks</c> for a shared lock/unlock transaction, <c>submit</c>). A project's write throughput is at most one
        /// transaction per this long.
        /// </summary>
        public static readonly Histogram<double> ProjectLockHeld = Meter.CreateHistogram("vertex.project_lock.held", "s", "Time a project's write lock is held, including the commit.", advice: Seconds);

        /// <summary>
        /// Commit duration of project writes, tagged with <c>operation</c>. This is the database's durable-write (fsync)
        /// cost; when it dominates <see cref="ProjectLockHeld"/>, the database's storage is the limit.
        /// </summary>
        public static readonly Histogram<double> CommitDuration = Meter.CreateHistogram("vertex.db.commit", "s", "Commit duration of project writes.", advice: Seconds);

        /// <summary>Lock and unlock requests that shared one transaction (and one commit).</summary>
        public static readonly Histogram<long> WriteBatchSize = Meter.CreateHistogram("vertex.project_writes.batch", "{request}", "Lock and unlock requests that shared one commit.", advice: BatchSizes);

        // ---- Events: the push pipeline.

        /// <summary>Events recorded, tagged with <c>type</c>.</summary>
        public static readonly Counter<long> EventsRecorded = Meter.CreateCounter<long>("vertex.events.recorded", "{event}", "Project events recorded.");

        /// <summary>Event messages written to client streams, tagged with <c>type</c>. Grows with events × subscribers.</summary>
        public static readonly Counter<long> EventsDelivered = Meter.CreateCounter<long>("vertex.events.delivered", "{message}", "Event messages sent to clients.");

        /// <summary>Time from an event being recorded to it being written to a client's stream.</summary>
        public static readonly Histogram<double> EventDeliveryDelay = Meter.CreateHistogram("vertex.events.delivery_delay", "s", "Time from recording an event to sending it to a client.", advice: Seconds);

        /// <summary>Clients following a project's event stream on this instance.</summary>
        public static readonly UpDownCounter<long> EventStreams = Meter.CreateUpDownCounter<long>("vertex.event_streams", "{stream}", "Open event streams.");

        // ---- Transfer and storage.

        /// <summary>Compressed chunk bytes received from clients.</summary>
        public static readonly Counter<long> BytesReceived = Meter.CreateCounter<long>("vertex.transfer.received", "By", "Compressed chunk bytes uploaded by clients.");

        /// <summary>Compressed chunk bytes sent to clients.</summary>
        public static readonly Counter<long> BytesSent = Meter.CreateCounter<long>("vertex.transfer.sent", "By", "Compressed chunk bytes downloaded by clients.");

        /// <summary>Chunks clients asked about before uploading, tagged <c>result</c> = new or deduplicated (already stored).</summary>
        public static readonly Counter<long> Chunks = Meter.CreateCounter<long>("vertex.transfer.chunks", "{chunk}", "Chunks checked before upload, new or already stored.");

        /// <summary>Files clients asked about before uploading, tagged <c>result</c> = new or deduplicated.</summary>
        public static readonly Counter<long> Files = Meter.CreateCounter<long>("vertex.transfer.files", "{file}", "Files checked before upload, new or already stored.");

        /// <summary>S3 calls, tagged with <c>operation</c> and <c>outcome</c> (ok, not_found, error).</summary>
        public static readonly Histogram<double> StorageDuration = Meter.CreateHistogram("vertex.storage.duration", "s", "S3 call duration.", advice: Seconds);

        /// <summary>Existence checks answered from memory instead of the content index, tagged <c>result</c> = hit or miss.</summary>
        public static readonly Counter<long> StorageCache = Meter.CreateCounter<long>("vertex.storage.cache", "{lookup}", "Existence checks answered from memory (hit) or the content index (miss).");

        /// <summary>HybridCache lookups, tagged <c>result</c> = hit or miss and <c>category</c> (key prefix).</summary>
        public static readonly Counter<long> CacheLookups = Meter.CreateCounter<long>("vertex.cache.lookups", "{lookup}", "HybridCache lookups answered from memory or Redis (hit) or the factory (miss).");

        /// <summary>Time spent producing a value on a cache miss, tagged with <c>category</c>.</summary>
        public static readonly Histogram<double> CacheFactoryDuration = Meter.CreateHistogram("vertex.cache.factory", "s", "Time producing a value on a cache miss.", advice: Seconds);

        /// <summary>
        /// Whole cache lookups, tagged with <c>category</c> and <c>result</c>. A hit that takes milliseconds came from
        /// Redis, so a Redis stall shows up here directly.
        /// </summary>
        public static readonly Histogram<double> CacheDuration = Meter.CreateHistogram("vertex.cache.duration", "s", "Cache lookup duration, including Redis and the factory.", advice: Seconds);

        /// <summary>Serialized size of values stored in the cache, tagged with <c>type</c>.</summary>
        public static readonly Histogram<long> CacheValueSize = Meter.CreateHistogram("vertex.cache.value_size", "By", "Serialized size of cached values.", advice: Bytes);

        /// <summary>Time transfers wait for a <c>TransferBudget</c> slot before touching S3.</summary>
        public static readonly Histogram<double> TransferBudgetWait = Meter.CreateHistogram("vertex.transfer.budget_wait", "s", "Time waiting for a transfer slot.", advice: Seconds);
        
        // ---- Dependencies and security.

        /// <summary>
        /// gRPC calls that failed with an exception, tagged with <c>method</c>, the <c>status</c> returned and the
        /// <c>exception</c> type (see <c>ExceptionInterceptor</c>).
        /// </summary>
        public static readonly Counter<long> GrpcErrors = Meter.CreateCounter<long>("vertex.grpc.errors", "{error}", "gRPC calls that failed with an exception.");

        /// <summary>Redis operations that failed, tagged with <c>operation</c>.</summary>
        public static readonly Counter<long> RedisErrors = Meter.CreateCounter<long>("vertex.redis.errors", "{error}", "Failed Redis operations.");

        /// <summary>Login attempts, tagged <c>outcome</c> = success, failed, disabled or throttled.</summary>
        public static readonly Counter<long> Logins = Meter.CreateCounter<long>("vertex.auth.logins", "{login}", "Login attempts.");

        // ---- Gauges read from the services that own the state.

        /// <summary>Reports whether Redis is connected (1) or not (0); set by the cache.</summary>
        public static Func<int>? RedisState { get; set; }

        /// <summary>Reports how many projects have live subscribers on this instance; set by the notification service.</summary>
        public static Func<int>? ActiveFeeds { get; set; }

        /// <summary>Reports, per event handler, how many events it is behind; set by the dispatcher.</summary>
        public static Func<IEnumerable<KeyValuePair<string, long>>>? HandlerLag { get; set; }

        /// <summary>Reports transfer slots in use and in total; set by the transfer budget.</summary>
        public static Func<(int InUse, int Total)>? TransferSlots { get; set; }

        static VertexMetrics()
        {
            Meter.CreateObservableGauge("vertex.redis.connected", () => RedisState?.Invoke() ?? 0, "1", "Whether Redis is connected (1) or not (0).");
            Meter.CreateObservableGauge("vertex.event_feeds", () => ActiveFeeds?.Invoke() ?? 0, "{project}", "Projects with live subscribers on this instance.");
            Meter.CreateObservableGauge("vertex.events.handler_lag", () => (HandlerLag?.Invoke() ?? [])
                .Select(lag => new Measurement<long>(lag.Value, new KeyValuePair<string, object?>("handler", lag.Key))), "{event}", "Events an event handler is behind, worst project.");
            Meter.CreateObservableGauge("vertex.transfer.slots_used", () => TransferSlots?.Invoke().InUse ?? 0, "{chunk}", "Transfer slots in use on this instance.");
            Meter.CreateObservableGauge("vertex.transfer.slots", () => TransferSlots?.Invoke().Total ?? 0, "{chunk}", "Transfer slots on this instance.");
        }

        /// <summary>
        /// Starts timing; dispose (or call <see cref="Timer.Stop"/>) to record the elapsed seconds.
        /// </summary>
        public static Timer Time(Histogram<double> histogram, string tag, string value)
        {
            return new Timer(histogram, new KeyValuePair<string, object?>(tag, value));
        }

        public static KeyValuePair<string, object?> Tag(string name, object? value)
        {
            return new KeyValuePair<string, object?>(name, value);
        }

        public readonly struct Timer(Histogram<double> histogram, KeyValuePair<string, object?> tag) : IDisposable
        {
            private readonly long _start = Stopwatch.GetTimestamp();

            public void Stop()
            {
                histogram.Record(Stopwatch.GetElapsedTime(_start).TotalSeconds, tag);
            }

            public void Dispose()
            {
                Stop();
            }
        }
    }
}
