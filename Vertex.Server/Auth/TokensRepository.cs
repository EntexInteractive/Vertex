// Copyright 2026 Entex Interactive

using Dapper;
using EntexInteractive.Extensions;
using Npgsql;
using Vertex.Server.Database;

namespace Vertex.Server.Auth
{
    /// <summary>
    /// What a valid token resolves to. Cached briefly so authenticating a call doesn't hit PostgreSQL.
    /// </summary>
    public sealed record TokenSession(Guid TokenId, Guid UserId, string Username, bool IsAdmin, long ExpiresAt);

    public class TokensRepository
    {
        // Short, so a cached session can't outlive a change to its user (disabled, admin flag removed) for long.
        // Those changes also evict the user's sessions explicitly.
        private const double SessionCacheMinutes = 1;

        private readonly PostgresDatabase _database;
        private readonly CacheContext _cache;

        public TokensRepository(PostgresDatabase database, CacheContext cache)
        {
            _database = database;
            _cache = cache;
        }

        /// <summary>
        /// Creates a token for a user.
        /// </summary>
        /// <param name="userId">The user the token belongs to.</param>
        /// <param name="name">A label for the token, usually the device's name.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <returns>The token. It's only available now; the server keeps just its hash.</returns>
        public async Task<string> CreateAsync(Guid userId, string name, CancellationToken cancellationToken)
        {
            string token = AccessTokens.Create();
            long now = (long)UnixTime.Now.TotalMilliseconds;
            const string sql = "INSERT INTO auth.tokens (id, created_at, user_id, token_hash, name, last_used_at, expires_at) " +
                               "VALUES (@Id, @Now, @UserId, @Hash, @Name, @Now, @ExpiresAt);";

            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                Id = Guid.NewGuid(),
                Now = now,
                UserId = userId,
                Hash = AccessTokens.Hash(token),
                Name = name.Length > 255 ? name[..255] : name,
                ExpiresAt = now + (long)AccessTokens.IdleLifetime.TotalMilliseconds
            }, cancellationToken: cancellationToken));

            return token;
        }

        /// <summary>
        /// Resolves a token to its session.
        /// </summary>
        /// <returns>Null when the token doesn't exist or its user is disabled. Expiry is left to the caller.</returns>
        public async Task<TokenSession?> FindSessionAsync(byte[] hash)
        {
            return await _cache.GetOrSetAsync(AccessTokens.CacheKey(hash), async cancellationToken =>
            {
                const string sql = "SELECT t.id AS token_id, u.id AS user_id, u.username, u.is_admin, t.expires_at " +
                                   "FROM auth.tokens t JOIN auth.users u ON u.id = t.user_id " +
                                   "WHERE t.token_hash = @Hash AND NOT u.disabled;";

                await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
                return await connection.QuerySingleOrDefaultAsync<TokenSession>(new CommandDefinition(sql, new { Hash = hash }, cancellationToken: cancellationToken));
            }, SessionCacheMinutes);
        }

        /// <summary>
        /// Records that a token was used and pushes its expiry forward.
        /// </summary>
        public async Task TouchAsync(Guid tokenId, CancellationToken cancellationToken)
        {
            long now = (long)UnixTime.Now.TotalMilliseconds;
            const string sql = "UPDATE auth.tokens SET last_used_at = @Now, expires_at = @ExpiresAt WHERE id = @Id;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = tokenId, Now = now, ExpiresAt = now + (long)AccessTokens.IdleLifetime.TotalMilliseconds }, cancellationToken: cancellationToken));
        }

        /// <summary>
        /// Deletes a token and evicts its cached session on every server instance.
        /// </summary>
        public async Task RevokeAsync(Guid tokenId, CancellationToken cancellationToken)
        {
            const string sql = "DELETE FROM auth.tokens WHERE id = @Id RETURNING token_hash;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            byte[]? hash = await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(sql, new { Id = tokenId }, cancellationToken: cancellationToken));
            if (hash is not null) await _cache.DeleteCachedValueAsync(AccessTokens.CacheKey(hash));
        }

        /// <summary>
        /// Deletes a user's tokens, except optionally the one making the request, logging them out everywhere else.
        /// </summary>
        public async Task RevokeAllAsync(Guid userId, Guid? exceptTokenId, CancellationToken cancellationToken)
        {
            const string sql = "DELETE FROM auth.tokens WHERE user_id = @UserId AND id IS DISTINCT FROM @Except RETURNING token_hash;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            IEnumerable<byte[]> hashes = await connection.QueryAsync<byte[]>(new CommandDefinition(sql, new { UserId = userId, Except = exceptTokenId }, cancellationToken: cancellationToken));
            foreach (byte[] hash in hashes) await _cache.DeleteCachedValueAsync(AccessTokens.CacheKey(hash));
        }

        /// <summary>
        /// Evicts the cached sessions of all of a user's tokens, so a change such as disabling the user or removing
        /// their admin flag applies to their next call.
        /// </summary>
        public async Task EvictUserSessionsAsync(Guid userId, CancellationToken cancellationToken)
        {
            const string sql = "SELECT token_hash FROM auth.tokens WHERE user_id = @UserId;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            IEnumerable<byte[]> hashes = await connection.QueryAsync<byte[]>(new CommandDefinition(sql, new { UserId = userId }, cancellationToken: cancellationToken));
            foreach (byte[] hash in hashes) await _cache.DeleteCachedValueAsync(AccessTokens.CacheKey(hash));
        }
    }
}
