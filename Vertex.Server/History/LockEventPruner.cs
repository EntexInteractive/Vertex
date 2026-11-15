// Copyright 2026 Entex Interactive

using Vertex.Server.Database;

namespace Vertex.Server.History
{
    /// <summary>
    /// Deletes old lock events. Clients whose cursor is older than the retained events get the full lock list
    /// instead, so pruning never loses a lock, it only makes a long-idle client's next poll a bit larger.
    /// </summary>
    public class LockEventPruner : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
        private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

        private readonly ILogger<LockEventPruner> _logger;
        private readonly IServiceScopeFactory _scopes;
        private readonly CacheContext _cache;

        public LockEventPruner(ILogger<LockEventPruner> logger, IServiceScopeFactory scopes, CacheContext cache)
        {
            _logger = logger;
            _scopes = scopes;
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // With Redis, one instance in the cluster prunes per interval; without it this instance is alone.
                    if (!_cache.HasRedis || await _cache.TrySetKeyAsync("locks:prune", Interval))
                    {
                        using IServiceScope scope = _scopes.CreateScope();
                        long before = DateTimeOffset.UtcNow.Subtract(Retention).ToUnixTimeMilliseconds();
                        int deleted = await scope.ServiceProvider.GetRequiredService<LocksRepository>().PruneEventsAsync(before, stoppingToken);
                        if (deleted > 0) _logger.LogInformation("Pruned {Count} lock event(s) older than {Days} days", deleted, Retention.TotalDays);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not prune lock events");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}
