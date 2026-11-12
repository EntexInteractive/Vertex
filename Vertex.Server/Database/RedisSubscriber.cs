// Copyright 2026 Entex Interactive

using Microsoft.Extensions.Caching.Hybrid;

namespace Vertex.Server.Database
{
    /// <summary>
    /// Represents the event notifier for Redis channels.
    /// </summary>
    public class RedisSubscriber : IHostedService
    {
        private readonly ILogger<RedisSubscriber> _logger;
        private readonly CacheContext _cache;
        private readonly HybridCache _hybrid;

        /// <summary>
        /// Create a new <see cref="RedisSubscriber"/> instance.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="cache"></param>
        /// <param name="hybrid"></param>
        public RedisSubscriber(ILogger<RedisSubscriber> logger, CacheContext cache, HybridCache hybrid)
        {
            _logger = logger;
            _cache = cache;
            _hybrid = hybrid;
        }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Subscribed to channel: {Channel}", _cache.CacheInvalidateChannel);
            await _cache.SubscribeAsync(_cache.CacheInvalidateChannel, (key) =>
            {
                _hybrid.RemoveAsync(key, cancellationToken);
            });
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Unsubscribed from channel: {Channel}", _cache.CacheInvalidateChannel);
            return Task.CompletedTask;
        }
    }
}