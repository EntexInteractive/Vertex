// Copyright 2026 Entex Interactive

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using Vertex.Server.Telemetry;

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
            VertexMetrics.RedisState = () => _redis?.IsConnected == true ? 1 : 0;

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
        /// Returns the cached value for <paramref name="key"/>, or runs <paramref name="factory"/> and caches what it
        /// returns, null included. Keys are case-sensitive; build them with <see cref="CacheKeys"/>.
        /// </summary>
        /// <param name="expires">How long the value lives, in minutes. Each instance keeps it in memory for a third of that.</param>
        public Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, double expires, CancellationToken cancellationToken = default)
        {
            return GetOrSetAsync(key, factory, TimeSpan.FromMinutes(expires), cancellationToken);
        }

        /// <inheritdoc cref="GetOrSetAsync{T}(string, Func{CancellationToken, ValueTask{T}}, double, CancellationToken)"/>
        public async Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, TimeSpan expires, CancellationToken cancellationToken = default)
        {
            string category = CategoryOf(key);
            bool factoryRan = false;

            try
            {
                return await _cache.GetOrCreateAsync(key, async ct =>
                {
                    factoryRan = true;
                    using VertexMetrics.Timer _ = VertexMetrics.Time(VertexMetrics.CacheFactoryDuration, "category", category);
                    return await factory(ct);
                }, new HybridCacheEntryOptions
                {
                    Expiration = expires,
                    LocalCacheExpiration = expires / 3
                }, cancellationToken: cancellationToken);
            }
            finally
            {
                VertexMetrics.CacheLookups.Add(1, VertexMetrics.Tag("result", factoryRan ? "miss" : "hit"), VertexMetrics.Tag("category", category));
            }
        }

        /// <summary>
        /// Like <see cref="GetOrSetAsync{T}(string, Func{CancellationToken, ValueTask{T}}, TimeSpan, CancellationToken)"/>,
        /// but only caches values that were found: a null result is returned and not cached, so the next call asks
        /// again. For immutable data that may be added later, such as manifests.
        /// </summary>
        public async Task<T?> GetOrSetIfFoundAsync<T>(string key, Func<CancellationToken, ValueTask<T?>> factory, TimeSpan expires, CancellationToken cancellationToken = default) where T : class
        {
            try
            {
                return await GetOrSetAsync<T>(key, async ct => await factory(ct) ?? throw new NotFoundSignal(), expires, cancellationToken);
            }
            catch (NotFoundSignal)
            {
                return null;
            }
        }

        // HybridCache never caches a factory that throws, so throwing is how a miss stays uncached.
        private sealed class NotFoundSignal : Exception;

        /// <summary>
        /// The current value of a generation key. Put it in other keys so one <see cref="BumpGenerationAsync"/> makes
        /// all of them miss, for values that too many writes affect to delete one by one (lists, access levels).
        /// </summary>
        public async Task<string> GetGenerationAsync(string key)
        {
            return await GetOrSetAsync(key, _ => ValueTask.FromResult(Guid.NewGuid().ToString("N")), GenerationLifetime) ?? string.Empty;
        }

        /// <summary>
        /// Starts new generations, so every key built from them misses on every instance.
        /// </summary>
        public Task BumpGenerationAsync(params string[] keys)
        {
            return DeleteCachedValuesAsync(keys);
        }

        private static readonly TimeSpan GenerationLifetime = TimeSpan.FromDays(1);

        // "project:42:branches" -> "project". Bounded cardinality; never tag with the full key.
        private static string CategoryOf(string key)
        {
            int i = key.IndexOf(':');
            return i > 0 ? key[..i] : "other";
        }

        /// <summary>
        /// True when Redis is connected. Without it every instance runs alone: <see cref="TrySetKeyAsync"/> always
        /// fails and shared values aren't available.
        /// </summary>
        public bool HasRedis => _redis is not null;

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
        /// Deletes cached values on every instance: from Redis, from this instance's memory, and (through the
        /// invalidation channel) from every other instance's memory.
        /// </summary>
        public async Task DeleteCachedValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Removed cache key: {CacheKey}", key);
            await _cache.RemoveAsync(key, cancellationToken);
            await PublishAsync(CacheInvalidateChannel, key);
        }

        /// <inheritdoc cref="DeleteCachedValueAsync"/>
        public async Task DeleteCachedValuesAsync(params string[] keys)
        {
            foreach (string key in keys.Distinct(StringComparer.Ordinal)) await DeleteCachedValueAsync(key);
        }

        // INCR and start the expiry on the first increment, atomically, so a crash between the two can't leave a
        // counter that never expires.
        private const string IncrementScript = "local count = redis.call('INCR', KEYS[1]) if count == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end return count";

        /// <summary>
        /// Adds one to a counter shared by every server instance. The counter starts at the first increment and
        /// disappears <paramref name="window"/> later.
        /// </summary>
        /// <returns>The new count, or null when Redis isn't available, so the caller can fall back to a local count.</returns>
        public async Task<long?> IncrementCounterAsync(string key, TimeSpan window)
        {
            if (_redis is null) return null;

            try
            {
                RedisResult result = await _redis.GetDatabase().ScriptEvaluateAsync(IncrementScript, [new RedisKey(key)], [(long)window.TotalMilliseconds]);
                return (long)result;
            }
            catch (RedisException ex)
            {
                VertexMetrics.RedisErrors.Add(1, VertexMetrics.Tag("operation", "increment"));
                _logger.LogWarning(ex, "Could not increment the shared counter '{Key}'.", key);
                return null;
            }
        }

        // Raise the stored number to ARGV[1] unless it is already higher, and refresh the expiry, atomically.
        private const string SetMaxScript = "local current = tonumber(redis.call('GET', KEYS[1])) " +
                                            "if current == nil or current < tonumber(ARGV[1]) then redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2]) end";

        /// <summary>
        /// Stores <paramref name="value"/> under a key shared by every server instance, unless a higher value is
        /// already stored, so updates that arrive out of order never move it backwards.
        /// </summary>
        public async Task SetMaxAsync(string key, long value, TimeSpan expires)
        {
            if (_redis is null) return;

            try
            {
                await _redis.GetDatabase().ScriptEvaluateAsync(SetMaxScript, [new RedisKey(key)], [value, (long)expires.TotalMilliseconds]);
            }
            catch (RedisException ex)
            {
                VertexMetrics.RedisErrors.Add(1, VertexMetrics.Tag("operation", "set_max"));
                _logger.LogWarning(ex, "Could not update the shared value '{Key}'.", key);
            }
        }

        /// <summary>
        /// Reads a number stored with <see cref="SetMaxAsync"/>.
        /// </summary>
        /// <returns>The value, or null when it isn't stored or Redis isn't available.</returns>
        public async Task<long?> GetInt64Async(string key)
        {
            if (_redis is null) return null;

            try
            {
                RedisValue value = await _redis.GetDatabase().StringGetAsync(new RedisKey(key));
                return value.HasValue && long.TryParse(value.ToString(), out long number) ? number : null;
            }
            catch (RedisException ex)
            {
                VertexMetrics.RedisErrors.Add(1, VertexMetrics.Tag("operation", "get"));
                _logger.LogWarning(ex, "Could not read the shared value '{Key}'.", key);
                return null;
            }
        }

        /// <summary>
        /// Reads several shared counters in one round trip. Missing counters read as 0.
        /// </summary>
        /// <returns>The counts in the order of <paramref name="keys"/>, or null when Redis isn't available.</returns>
        public async Task<long[]?> GetCountersAsync(params string[] keys)
        {
            if (_redis is null) return null;

            try
            {
                RedisValue[] values = await _redis.GetDatabase().StringGetAsync(keys.Select(k => new RedisKey(k)).ToArray());
                return values.Select(v => v.HasValue ? (long)v : 0).ToArray();
            }
            catch (RedisException ex)
            {
                VertexMetrics.RedisErrors.Add(1, VertexMetrics.Tag("operation", "get_counters"));
                _logger.LogWarning(ex, "Could not read shared counters.");
                return null;
            }
        }

        /// <summary>
        /// Deletes a shared counter.
        /// </summary>
        /// <returns>False when Redis isn't available.</returns>
        public async Task<bool> DeleteCounterAsync(string key)
        {
            if (_redis is null) return false;

            try
            {
                await _redis.GetDatabase().KeyDeleteAsync(new RedisKey(key));
                return true;
            }
            catch (RedisException ex)
            {
                VertexMetrics.RedisErrors.Add(1, VertexMetrics.Tag("operation", "delete"));
                _logger.LogWarning(ex, "Could not delete the shared counter '{Key}'.", key);
                return false;
            }
        }

        /// <inheritdoc />
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
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
            return _redis?.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, value) =>
            {
                if (!value.HasValue) return;
                callback(value.ToString());
            }) ?? Task.CompletedTask;
        }


        /// <summary>
        /// Subscribes to every channel matching a pattern such as <c>events:*</c>, one subscription for all of them.
        /// </summary>
        /// <param name="callback">Gets the channel name and the message.</param>
        public Task SubscribePatternAsync(string pattern, Action<string, string> callback)
        {
            return _redis?.GetSubscriber().SubscribeAsync(RedisChannel.Pattern(pattern), (channel, value) =>
            {
                if (value.HasValue) callback(channel.ToString(), value.ToString());
            }) ?? Task.CompletedTask;
        }

        /// <summary>
        /// Publishes a message, reporting failure instead of throwing.
        /// </summary>
        /// <returns>False when Redis isn't available or the publish failed.</returns>
        public async Task<bool> TryPublishAsync(string channel, RedisValue message)
        {
            if (_redis is null) return false;

            try
            {
                await _redis.GetSubscriber().PublishAsync(RedisChannel.Literal(channel), message);
                return true;
            }
            catch (RedisException ex)
            {
                VertexMetrics.RedisErrors.Add(1, VertexMetrics.Tag("operation", "publish"));
                _logger.LogWarning(ex, "Could not publish to '{Channel}'.", channel);
                return false;
            }
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