// Copyright 2026 Entex Interactive

using Vertex.Server.Database;

namespace Vertex.Server.Events
{
    /// <summary>
    /// Deletes project events older than a week. Nothing that matters is lost: clients whose cursor is older reload
    /// the state they mirror (the lock list), and handlers that were down that long skip ahead with a warning.
    /// </summary>
    public class EventPruner : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
        private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

        private readonly ILogger<EventPruner> _logger;
        private readonly EventStore _store;
        private readonly CacheContext _cache;

        public EventPruner(ILogger<EventPruner> logger, EventStore store, CacheContext cache)
        {
            _logger = logger;
            _store = store;
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // With Redis, one instance in the cluster prunes per interval; without it this instance is alone.
                    if (!_cache.HasRedis || await _cache.TrySetKeyAsync("events:prune", Interval))
                    {
                        long before = DateTimeOffset.UtcNow.Subtract(Retention).ToUnixTimeMilliseconds();
                        int deleted = await _store.PruneAsync(before, stoppingToken);
                        if (deleted > 0) _logger.LogInformation("Pruned {Count} event(s) older than {Days} days", deleted, Retention.TotalDays);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not prune events");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}
