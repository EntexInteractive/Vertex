// Copyright 2026 Entex Interactive

using System.Data;
using Dapper;

namespace Vertex.Server.Database
{
    /// <summary>
    /// Represents the centralized context for data access using Dapper and Redis.
    /// </summary>
    public class DapperContext
    {
        private readonly ILogger<DapperContext> _logger;
        private readonly CacheContext _cache;

        /// <summary>
        /// Creates a new <see cref="DapperContext"/> class.
        /// </summary>
        /// <param name="logger"></param>>
        /// <param name="cache"></param>
        public DapperContext(ILogger<DapperContext> logger, CacheContext cache)
        {
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// Execute a command asynchronously and removes any cached value if one exists for the given key.
        /// </summary>
        /// <param name="sql">The SQL to execute for this query</param>
        /// <param name="connectionFactory"></param>
        /// <param name="parameters">The parameters to use for this query.</param>
        /// <param name="key">The key used for caching.</param>
        /// <returns>True if success, otherwise false.</returns>
        public async Task<bool> ExecuteAsync(Func<IDbConnection> connectionFactory, string sql, object? parameters, string? key = null)
        {
            try
            {
                using IDbConnection connection = connectionFactory();
                bool success = await connection.ExecuteAsync(sql, parameters) > 0;
                if (!string.IsNullOrEmpty(key) && success) await _cache.DeleteCachedValueAsync(key);
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying database");
                return false;
            }
        }

        /// <summary>
        /// Execute a single-row query bypassing dapper's object serialization for "application/json" responses.
        /// </summary>
        /// <param name="connectionFactory"></param>
        /// <param name="sql"></param>
        /// <param name="parameters"></param>
        /// <param name="key"></param>
        /// <param name="expires"></param>
        public async Task<string?> QueryJsonAsync(Func<IDbConnection> connectionFactory, string sql, object? parameters, string? key = null, int expires = 15)
        {
            return await QuerySingleAsync<string>(connectionFactory, sql, parameters, key, expires);
        }

        /// <summary>
        /// Execute a single-row query asynchronously by first checking for a cached value.
        /// </summary>
        /// <param name="connectionFactory"></param>
        /// <param name="sql">The SQL to execute for this query</param>
        /// <param name="parameters">The parameters to use for this query.</param>
        /// <param name="key">The key to use for caching.</param>
        /// <param name="expires">The expiry timeout for the cached value (in minutes).</param>
        /// <typeparam name="T">The type used to determine the cache key prefix.</typeparam>
        public async Task<T?> QuerySingleAsync<T>(Func<IDbConnection> connectionFactory, string sql, object? parameters, string? key = null, int expires = 15)
        {
            try
            {
                if (!string.IsNullOrEmpty(key))
                {
                    return await _cache.GetOrSetAsync(key, async ct =>
                        {
                            using IDbConnection connection = connectionFactory();
                            return await connection.QuerySingleOrDefaultAsync<T>(sql, parameters);
                        },
                        expires);
                }

                using IDbConnection connectionFallback = connectionFactory();
                return await connectionFallback.QuerySingleOrDefaultAsync<T>(sql, parameters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying database");
                return default;
            }
        }

        /// <summary>
        /// Execute a query asynchronously by first checking for a cached value.
        /// </summary>
        /// <param name="connectionFactory"></param>
        /// <param name="sql">The SQL to execute for this query</param>
        /// <param name="parameters">The parameters to use for this query.</param>
        /// <param name="key">The key to use for caching.</param>
        /// <param name="expires">The expiry timeout for the cached value (in minutes).</param>
        /// <typeparam name="TSource">The type used to determine the cache key prefix.</typeparam>
        public async Task<IReadOnlyList<TSource>> QueryAsync<TSource>(Func<IDbConnection> connectionFactory, string sql, object? parameters, string? key = null, int expires = 15)
        {
            try
            {
                async ValueTask<IReadOnlyList<TSource>> Execute(CancellationToken ct)
                {
                    using IDbConnection connection = connectionFactory();
                    return (await connection.QueryAsync<TSource>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToReadOnly();
                }

                if (!string.IsNullOrEmpty(key))
                {
                    return await _cache.GetOrSetAsync(key, Execute, expires) ?? [];
                }

                using IDbConnection connectionFallback = connectionFactory();
                return (await connectionFallback.QueryAsync<TSource>(sql, parameters)).ToReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying database");
                return [];
            }
        }
    }

    /// <summary>
    /// Represents extension methods for <see cref="IEnumerable{T}"/>.
    /// </summary>
    public static class DapperExtensions
    {
        /// <summary>
        /// Returns a materialized read-only collection
        /// </summary>
        /// <typeparam name="TSource"></typeparam>
        /// <param name="source"></param>
        /// <returns></returns>
        public static IReadOnlyList<TSource> ToReadOnly<TSource>(this IEnumerable<TSource> source)
        {
            return source.ToList().AsReadOnly();
        }
    }
}