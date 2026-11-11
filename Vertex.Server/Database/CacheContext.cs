// Copyright 2026 Entex Interactive

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Vertex.Server.Database
{
    /// <summary>
    /// Represents a caching context using redis.
    /// </summary>
    public class CacheContext : IHealthCheck
    {
        public string CacheInvalidateChannel = "cache:invalidate";
        
        private readonly ILogger<CacheContext> _logger;
        private readonly IConnectionMultiplexer? _redis;
        private readonly HybridCache _cache;

        /// <summary>
        /// Initializes a new instance of the <see cref="CacheContext"/> class.
        /// </summary>
        public CacheContext(ILogger<CacheContext> logger, HybridCache cache, ServerSettings settings)
        {
            _logger = logger;
            _cache = cache;

            try
            {
                _redis = ConnectionMultiplexer.Connect(settings.RedisConnectionString);

                _logger.LogInformation("Connected to redis at '{FirstOrDefault}'. Caching will be enabled.", _redis.GetEndPoints().FirstOrDefault());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"It was not possible to connect to the redis server(s). HybridCache will run memory-only.");
            }
        }

        /// <summary>
        /// Gets cached data.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="factory"></param>
        /// <param name="expires"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public async Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, double expires)
        {
            return await _cache.GetOrCreateAsync(key.ToLowerInvariant(), factory, new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(expires),
                LocalCacheExpiration = TimeSpan.FromMinutes(expires / 3)
            });
        }

        /// <summary>
        /// Sets a key for a duration of minutes. Used to run on only 1 machine in the cluster.
        /// </summary>
        /// <param name="key">The key to use to retrieve the cached data.</param>
        /// <param name="expires">The data to be serialized and cached.</param>
        /// <returns>True if successful, otherwise false.</returns>
        public async Task<bool> TrySetKeyAsync(string key, TimeSpan expires)
        {
            string redisKey = key.ToLowerInvariant();
            return await (_redis?.GetDatabase().StringSetAsync(new RedisKey(redisKey), "locked", expires, When.NotExists) ?? Task.FromResult(false));
        }

        /// <summary>
        /// Deletes cached data.
        /// </summary>
        /// <param name="key"></param>
        public async Task DeleteCachedValueAsync(string key)
        {
            string cacheKey = key.ToLowerInvariant();
            _logger.LogDebug("Remove redis key: {CacheKey}", cacheKey);

            await (_redis?.GetDatabase().KeyDeleteAsync(new RedisKey(cacheKey)) ?? Task.CompletedTask);
            await PublishAsync(CacheInvalidateChannel, cacheKey.ToLowerInvariant());
            await _cache.RemoveAsync(cacheKey);
        }

        /// <inheritdoc />
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
        {
            try
            {
                TimeSpan span = await (_redis?.GetDatabase().PingAsync() ?? Task.FromResult(TimeSpan.MinValue));
                return HealthCheckResult.Healthy(data: new Dictionary<string, object> { ["Latency"] = span.TotalMilliseconds });
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Degraded("Unable to ping Redis", ex);
            }
        }

        /// <summary>
        /// Subscribe to notifications on a channel.
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="callback"></param>
        public Task SubscribeAsync(string channel, Action<string> callback)
        {
            _logger.LogInformation("Subscribed to channel: '{Channel}'", channel);
            Task subscribeAsync = _redis?.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, value) =>
            {
                if (!value.HasValue) return;
                callback(value.ToString());
            }) ?? Task.CompletedTask;

            return subscribeAsync;
        }


        /// <summary>
        /// Publish a message to a channel.
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="message"></param>
        public Task PublishAsync(string channel, RedisValue message)
        {
            return _redis?.GetSubscriber().PublishAsync(RedisChannel.Literal(channel), message) ?? Task.CompletedTask;
        }
    }
}