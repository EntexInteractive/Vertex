// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Vertex.Server.Database;
using Vertex.Server.Telemetry;

namespace Vertex.Server.Events
{
    /// <summary>
    /// Tells everything that follows a project that it has new events: streams to clients and server-side handlers.
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// Announces that a project's events up to <paramref name="seq"/> committed. Call it after the commit; the
        /// journal is the truth, this is only a wake-up, so a lost notification delays but never loses an event.
        /// </summary>
        Task NotifyAsync(Guid projectId, long seq);

        /// <summary>
        /// The project's events after <paramref name="afterSeq"/>: those already recorded, then new ones as they
        /// commit, until cancelled.
        /// </summary>
        IAsyncEnumerable<ProjectEvent> SubscribeAsync(Guid projectId, long afterSeq, CancellationToken ct);

        /// <summary>
        /// Completes when any project gets new events (or after a while anyway).
        /// </summary>
        Task WaitForAnyAsync(CancellationToken ct);

        /// <summary>
        /// The project's latest seq, and whether a subscriber at <paramref name="afterSeq"/> can resume (the events after
        /// it are still kept). Answered from the shared per-project feed, so a thousand clients connecting at once cost
        /// one database query, not a thousand.
        /// </summary>
        Task<(long Latest, bool Resumable)> PrepareAsync(Guid projectId, long afterSeq, CancellationToken ct);
    }

    /// <summary>
    /// Pub/sub for project events across server instances.
    /// </summary>
    /// <remarks>
    /// After a commit the writer publishes the new seq on Redis (<c>events:{project}</c>). Every instance holds one
    /// pattern subscription and, per project with live subscribers on it, one <see cref="ProjectFeed"/> that reads the
    /// new events from the journal once and hands them to all its local streams, so a thousand clients following a
    /// project cost one read per event, not a thousand. Without Redis there is a single instance and it wakes its feeds
    /// directly. Feeds also re-check the journal every <see cref="SafetyInterval"/> in case a notification was lost.
    /// </remarks>
    public sealed class NotificationService : INotificationService, IHostedService, IDisposable
    {
        private const string ChannelPrefix = "events:";
        private static readonly TimeSpan SafetyInterval = TimeSpan.FromSeconds(30);

        private readonly ILogger<NotificationService> _logger;
        private readonly CacheContext _cache;
        private readonly EventStore _store;
        private readonly ConcurrentDictionary<Guid, ProjectFeed> _feeds = new();
        private readonly object _anyGate = new();
        private TaskCompletionSource _any = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Timer? _safety;

        public NotificationService(ILogger<NotificationService> logger, CacheContext cache, EventStore store)
        {
            _logger = logger;
            _cache = cache;
            VertexMetrics.ActiveFeeds = () => _feeds.Count;
            _store = store;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_cache.HasRedis)
            {
                await _cache.SubscribePatternAsync(ChannelPrefix + "*", (channel, value) =>
                {
                    if (Guid.TryParseExact(channel.AsSpan(ChannelPrefix.Length), "N", out Guid projectId) && long.TryParse(value, out long seq))
                        OnNotified(projectId, seq);
                });
            }

            _safety = new Timer(_ =>
            {
                foreach (ProjectFeed feed in _feeds.Values) _ = FetchAsync(feed, long.MaxValue);
            }, null, SafetyInterval, SafetyInterval);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _safety?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _safety?.Dispose();
        }

        public async Task NotifyAsync(Guid projectId, long seq)
        {
            if (_cache.HasRedis)
            {
                // Redis delivers it to this instance's own subscription too.
                if (await _cache.TryPublishAsync(ChannelPrefix + projectId.ToString("N"), seq)) return;
            }

            OnNotified(projectId, seq);
        }

        public async IAsyncEnumerable<ProjectEvent> SubscribeAsync(Guid projectId, long afterSeq, [EnumeratorCancellation] CancellationToken ct)
        {
            ProjectFeed feed = Acquire(projectId);
            try
            {
                await feed.InitializeAsync(_store, ct);
                long cursor = afterSeq;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    // Take the wake-up before reading, so an event committed in between can't be missed.
                    Task changed = feed.Changed;
                    IReadOnlyList<ProjectEvent> batch = feed.After(cursor) ?? await _store.ReadAsync(projectId, cursor, ProjectFeed.Capacity, ct);
                    if (batch.Count == 0)
                    {
                        await changed.WaitAsync(ct);
                        continue;
                    }

                    foreach (ProjectEvent projectEvent in batch)
                    {
                        yield return projectEvent;
                        cursor = projectEvent.Seq;
                    }
                }
            }
            finally
            {
                Release(projectId, feed);
            }
        }

        public async Task<(long Latest, bool Resumable)> PrepareAsync(Guid projectId, long afterSeq, CancellationToken ct)
        {
            ProjectFeed feed = Acquire(projectId);
            try
            {
                await feed.InitializeAsync(_store, ct);
                long latest = feed.Latest;
                if (afterSeq <= 0 || afterSeq == latest) return (latest, true);
                if (afterSeq > latest) return (latest, false); // A cursor from another database (or a reset one).
                if (feed.Holds(afterSeq + 1)) return (latest, true);
                return (latest, await _store.ExistsAsync(projectId, afterSeq + 1, ct));
            }
            finally
            {
                Release(projectId, feed);
            }
        }

        public Task WaitForAnyAsync(CancellationToken ct)
        {
            lock (_anyGate) return _any.Task.WaitAsync(ct);
        }

        private void OnNotified(Guid projectId, long seq)
        {
            lock (_anyGate)
            {
                TaskCompletionSource any = _any;
                _any = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                any.TrySetResult();
            }

            if (_feeds.TryGetValue(projectId, out ProjectFeed? feed)) _ = FetchAsync(feed, seq);
        }

        private async Task FetchAsync(ProjectFeed feed, long seq)
        {
            try
            {
                await feed.FetchAsync(_store, seq, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read new events for project {ProjectId}", feed.ProjectId);
            }
        }

        private ProjectFeed Acquire(Guid projectId)
        {
            while (true)
            {
                ProjectFeed feed = _feeds.GetOrAdd(projectId, id => new ProjectFeed(id));
                if (feed.TryAddSubscriber()) return feed;

                // It was just emptied and is being removed; make a new one.
                _feeds.TryRemove(new KeyValuePair<Guid, ProjectFeed>(projectId, feed));
            }
        }

        private void Release(Guid projectId, ProjectFeed feed)
        {
            if (feed.RemoveSubscriber()) _feeds.TryRemove(new KeyValuePair<Guid, ProjectFeed>(projectId, feed));
        }
    }

    /// <summary>
    /// One project's recent events on this instance, shared by all its local subscribers.
    /// </summary>
    internal sealed class ProjectFeed(Guid projectId)
    {
        /// <summary>Recent events kept in memory; subscribers further behind read the journal directly.</summary>
        public const int Capacity = 1_000;

        private readonly object _gate = new();
        private readonly SemaphoreSlim _fetch = new(1, 1);
        private readonly List<ProjectEvent> _recent = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _latest;
        private bool _initialized;
        private int _subscribers;
        private bool _closed;

        public Guid ProjectId { get; } = projectId;

        /// <summary>The latest seq this feed has read (or found when it started).</summary>
        public long Latest
        {
            get
            {
                lock (_gate) return _latest;
            }
        }

        /// <summary>True when event <paramref name="seq"/> is in memory.</summary>
        public bool Holds(long seq)
        {
            lock (_gate) return _recent.Count > 0 && seq >= _recent[0].Seq && seq <= _recent[^1].Seq;
        }

        public Task Changed
        {
            get
            {
                lock (_gate) return _changed.Task;
            }
        }

        public bool TryAddSubscriber()
        {
            lock (_gate)
            {
                if (_closed) return false;
                _subscribers++;
                return true;
            }
        }

        /// <returns>True when that was the last subscriber and the feed is closed.</returns>
        public bool RemoveSubscriber()
        {
            lock (_gate)
            {
                _subscribers--;
                if (_subscribers > 0) return false;
                _closed = true;
                return true;
            }
        }

        public async Task InitializeAsync(EventStore store, CancellationToken ct)
        {
            if (_initialized) return;
            await _fetch.WaitAsync(ct);
            try
            {
                if (_initialized) return;
                long latest = await store.GetLatestSeqAsync(ProjectId, ct);
                lock (_gate)
                {
                    _latest = latest;
                    _initialized = true;
                }
            }
            finally
            {
                _fetch.Release();
            }
        }

        /// <summary>
        /// Events after <paramref name="cursor"/> from memory, an empty list when there are none yet, or null when the
        /// cursor is older than what's kept (read the journal instead).
        /// </summary>
        public IReadOnlyList<ProjectEvent>? After(long cursor)
        {
            lock (_gate)
            {
                if (!_initialized) return null;
                if (cursor >= _latest) return [];
                if (_recent.Count == 0 || cursor < _recent[0].Seq - 1) return null;
                return _recent.Where(e => e.Seq > cursor).ToList();
            }
        }

        /// <summary>
        /// Reads events newer than the ones held, once for every subscriber, and wakes them.
        /// </summary>
        /// <param name="seq">The seq that was announced; nothing is read when it's already held.</param>
        public async Task FetchAsync(EventStore store, long seq, CancellationToken ct)
        {
            await _fetch.WaitAsync(ct);
            try
            {
                if (!_initialized) return;
                while (true)
                {
                    long latest;
                    lock (_gate) latest = _latest;
                    if (seq != long.MaxValue && seq <= latest) return;

                    IReadOnlyList<ProjectEvent> events = await store.ReadAsync(ProjectId, latest, Capacity, ct);
                    if (events.Count == 0) return;

                    TaskCompletionSource changed;
                    lock (_gate)
                    {
                        _recent.AddRange(events);
                        if (_recent.Count > Capacity) _recent.RemoveRange(0, _recent.Count - Capacity);
                        _latest = events[^1].Seq;
                        changed = _changed;
                        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    changed.TrySetResult();
                    if (events.Count < Capacity) return;
                }
            }
            finally
            {
                _fetch.Release();
            }
        }
    }
}
