// Copyright 2026 Entex Interactive

using Dapper;
using Npgsql;

namespace Vertex.Server.Database
{
    /// <summary>
    /// Runs single statements against PostgreSQL, answering reads from the cache first when they're given a key.
    /// </summary>
    /// <remarks>
    /// Reads take an optional <c>key</c> (from <see cref="CacheKeys"/>): with one, the value comes from memory or Redis
    /// and only a miss queries PostgreSQL; without one, the query always runs. Null results are cached too, so every
    /// write that can turn a "not found" into a row must invalidate that key. Writes take the keys they make stale and
    /// invalidate them after the statement succeeds; multi-statement transactions call <see cref="InvalidateAsync"/>
    /// or <see cref="BumpAsync"/> after their commit. Errors are logged and returned as default (or false), and a
    /// failed query is never cached.
    /// </remarks>
    public class DapperContext
    {
        /// <summary>
        /// How long a cached read lives when the caller doesn't say. Writes invalidate their keys, so this only bounds
        /// how long a change made outside the server (by hand in psql) can go unseen.
        /// </summary>
        public static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(15);

        private readonly ILogger<DapperContext> _logger;
        private readonly PostgresDatabase _database;
        private readonly CacheContext _cache;

        public DapperContext(ILogger<DapperContext> logger, PostgresDatabase database, CacheContext cache)
        {
            _logger = logger;
            _database = database;
            _cache = cache;
        }

        /// <summary>
        /// Runs a write, then invalidates <paramref name="invalidate"/> when it changed at least one row.
        /// </summary>
        /// <returns>True when at least one row changed; false when none did or the statement failed.</returns>
        public async Task<bool> ExecuteAsync(string sql, object? parameters, CancellationToken cancellationToken, params string[] invalidate)
        {
            try
            {
                await using NpgsqlConnection connection = _database.CreateConnection();
                bool changed = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)) > 0;
                if (changed) await _cache.DeleteCachedValuesAsync(invalidate);
                return changed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error writing to the database");
                return false;
            }
        }

        /// <summary>
        /// Reads one row (or none), from the cache when <paramref name="key"/> is given.
        /// </summary>
        /// <returns>The row, or default when there is none or the query failed.</returns>
        public Task<T?> QuerySingleAsync<T>(string sql, object? parameters, string? key = null, TimeSpan? expires = null, CancellationToken cancellationToken = default)
        {
            return CachedAsync(key, expires, async (connection, ct) => await connection.QuerySingleOrDefaultAsync<T>(Command(sql, parameters, ct)), cancellationToken);
        }

        /// <summary>
        /// Reads rows, from the cache when <paramref name="key"/> is given.
        /// </summary>
        /// <returns>The rows, or an empty list when the query failed.</returns>
        public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters, string? key = null, TimeSpan? expires = null, CancellationToken cancellationToken = default)
        {
            return await CachedAsync<IReadOnlyList<T>>(key, expires, async (connection, ct) => (await connection.QueryAsync<T>(Command(sql, parameters, ct))).ToReadOnly(), cancellationToken) ?? [];
        }

        /// <summary>
        /// Reads the first column of the first row, such as a <c>SELECT EXISTS</c> or <c>COUNT</c>, from the cache
        /// when <paramref name="key"/> is given.
        /// </summary>
        public Task<T?> ExecuteScalarAsync<T>(string sql, object? parameters, string? key = null, TimeSpan? expires = null, CancellationToken cancellationToken = default)
        {
            return CachedAsync(key, expires, async (connection, ct) => await connection.ExecuteScalarAsync<T>(Command(sql, parameters, ct)), cancellationToken);
        }

        /// <summary>
        /// Reads a single JSON document built by PostgreSQL, bypassing Dapper's object mapping for "application/json"
        /// responses.
        /// </summary>
        public Task<string?> QueryJsonAsync(string sql, object? parameters, string? key = null, TimeSpan? expires = null, CancellationToken cancellationToken = default)
        {
            return QuerySingleAsync<string>(sql, parameters, key, expires, cancellationToken);
        }

        /// <summary>
        /// Invalidates cached values on every instance. Call after committing a transaction that made them stale.
        /// </summary>
        public Task InvalidateAsync(params string[] keys)
        {
            return _cache.DeleteCachedValuesAsync(keys);
        }

        /// <summary>
        /// Starts new generations (see <see cref="CacheContext.GetGenerationAsync"/>). Call after committing a change
        /// that affects every value built from them.
        /// </summary>
        public Task BumpAsync(params string[] generationKeys)
        {
            return _cache.BumpGenerationAsync(generationKeys);
        }

        /// <summary>
        /// The current value of a generation key, to build other keys from.
        /// </summary>
        public Task<string> GenerationAsync(string generationKey)
        {
            return _cache.GetGenerationAsync(generationKey);
        }

        private async Task<T?> CachedAsync<T>(string? key, TimeSpan? expires, Func<NpgsqlConnection, CancellationToken, Task<T>> query, CancellationToken cancellationToken)
        {
            async ValueTask<T> Run(CancellationToken ct)
            {
                await using NpgsqlConnection connection = _database.CreateConnection();
                return await query(connection, ct);
            }

            try
            {
                if (string.IsNullOrEmpty(key)) return await Run(cancellationToken);
                return await _cache.GetOrSetAsync(key, Run, expires ?? DefaultExpiry, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error querying the database");
                return default;
            }
        }

        private static CommandDefinition Command(string sql, object? parameters, CancellationToken cancellationToken)
        {
            return new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
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
        public static IReadOnlyList<TSource> ToReadOnly<TSource>(this IEnumerable<TSource> source)
        {
            return source.ToList().AsReadOnly();
        }
    }
}
