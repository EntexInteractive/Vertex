// Copyright 2026 Entex Interactive

using Dapper;
using EntexInteractive.Extensions;
using Npgsql;
using Vertex.Core.Data;
using Vertex.Server.Database;

namespace Vertex.Server.Users
{
    /// <summary>
    /// The fields needed to check a password, which never leave the server.
    /// </summary>
    public sealed record UserCredentials(Guid Id, string? PasswordHash, bool Disabled);

    public class UsersRepository
    {
        private const string UserColumns = "id, created_at, username, display_name, email, is_admin, disabled, last_login_at";

        private readonly PostgresDatabase _database;
        private readonly DapperContext _dapper;

        public UsersRepository(PostgresDatabase database, DapperContext dapper)
        {
            _database = database;
            _dapper = dapper;
        }

        /// <summary>
        /// Serializes creating users and groups, and reports whether a name is used by either. User and group names
        /// share one namespace so a grant can name either one. Call inside a transaction.
        /// </summary>
        public static async Task<bool> LockAndCheckNameTakenAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name, CancellationToken cancellationToken)
        {
            // An arbitrary key; the lock is released when the transaction ends.
            const string sql = "SELECT pg_advisory_xact_lock(7340001); " +
                               "SELECT EXISTS (SELECT 1 FROM auth.users WHERE lower(username) = lower(@Name)) " +
                               "OR EXISTS (SELECT 1 FROM auth.groups WHERE lower(name) = lower(@Name));";

            await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(sql, new { Name = name }, transaction, cancellationToken: cancellationToken));
            await results.ReadAsync();
            return await results.ReadSingleAsync<bool>();
        }

        /// <summary>
        /// Creates a user.
        /// </summary>
        /// <returns>The new user, or null when a user or group already has that name.</returns>
        public async Task<VUser?> CreateUserAsync(string username, string passwordHash, bool isAdmin, string? displayName, string? email, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (await LockAndCheckNameTakenAsync(connection, transaction, username, cancellationToken)) return null;

            string sql = "INSERT INTO auth.users (id, created_at, username, display_name, email, password_hash, is_admin, disabled) " +
                         "VALUES (@Id, @CreatedAt, @Username, @DisplayName, @Email, @PasswordHash, @IsAdmin, FALSE) " +
                         $"RETURNING {UserColumns};";

            VUser user = await connection.QuerySingleAsync<VUser>(new CommandDefinition(sql, new
            {
                Id = Guid.NewGuid(),
                CreatedAt = (long)UnixTime.Now.TotalMilliseconds,
                Username = username,
                DisplayName = displayName,
                Email = email,
                PasswordHash = passwordHash,
                IsAdmin = isAdmin
            }, transaction, cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);

            // A lookup before the user existed may have cached "not found".
            await _dapper.InvalidateAsync(CacheKeys.UserByName(username));
            await _dapper.BumpAsync(CacheKeys.UserListGeneration);
            return user;
        }

        /// <summary>
        /// Creates the first server admin, but only while there are no users at all. Safe when several server
        /// instances start at once: the unique username index lets only one insert win.
        /// </summary>
        /// <returns>True when the admin was created.</returns>
        public async Task<bool> CreateFirstAdminAsync(string username, string passwordHash, CancellationToken cancellationToken)
        {
            const string sql = "INSERT INTO auth.users (id, created_at, username, password_hash, is_admin, disabled) " +
                               "SELECT @Id, @CreatedAt, @Username, @PasswordHash, TRUE, FALSE " +
                               "WHERE NOT EXISTS (SELECT 1 FROM auth.users) ON CONFLICT DO NOTHING;";

            bool created = await _dapper.ExecuteAsync(sql, new
            {
                Id = Guid.NewGuid(),
                CreatedAt = (long)UnixTime.Now.TotalMilliseconds,
                Username = username,
                PasswordHash = passwordHash
            }, cancellationToken, CacheKeys.UserByName(username));

            if (created) await _dapper.BumpAsync(CacheKeys.UserListGeneration);
            return created;
        }

        public async Task<bool> AnyUsersAsync(CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM auth.users);", cancellationToken: cancellationToken));
        }

        public async Task<IReadOnlyList<VUser>> ListUsersAsync(CancellationToken cancellationToken)
        {
            const string sql = $"SELECT {UserColumns} FROM auth.users ORDER BY lower(username);";
            string generation = await _dapper.GenerationAsync(CacheKeys.UserListGeneration);
            return await _dapper.QueryAsync<VUser>(sql, null, CacheKeys.UserList(generation), cancellationToken: cancellationToken);
        }

        public async Task<VUser?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            const string sql = $"SELECT {UserColumns} FROM auth.users WHERE id = @Id;";
            return await _dapper.QuerySingleAsync<VUser>(sql, new { Id = id }, CacheKeys.UserById(id), cancellationToken: cancellationToken);
        }

        public async Task<VUser?> GetUserByNameAsync(string username, CancellationToken cancellationToken)
        {
            const string sql = $"SELECT {UserColumns} FROM auth.users WHERE lower(username) = lower(@Username);";
            return await _dapper.QuerySingleAsync<VUser>(sql, new { Username = username }, CacheKeys.UserByName(username), cancellationToken: cancellationToken);
        }

        // Credentials, password hashes and the active-admin count are never cached: they decide logins and guard the
        // last admin, so a disable, password change or demotion must count at once.

        public async Task<UserCredentials?> GetCredentialsAsync(string username, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id, password_hash, disabled FROM auth.users WHERE lower(username) = lower(@Username);";
            await using NpgsqlConnection connection = _database.CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<UserCredentials>(new CommandDefinition(sql, new { Username = username }, cancellationToken: cancellationToken));
        }

        public async Task<string?> GetPasswordHashAsync(Guid id, CancellationToken cancellationToken)
        {
            const string sql = "SELECT password_hash FROM auth.users WHERE id = @Id;";
            await using NpgsqlConnection connection = _database.CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
        }

        public async Task SetPasswordHashAsync(Guid id, string passwordHash, CancellationToken cancellationToken)
        {
            const string sql = "UPDATE auth.users SET password_hash = @PasswordHash WHERE id = @Id;";
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = id, PasswordHash = passwordHash }, cancellationToken: cancellationToken));
        }

        public async Task SetLastLoginAsync(Guid id, CancellationToken cancellationToken)
        {
            const string sql = "UPDATE auth.users SET last_login_at = @Now WHERE id = @Id RETURNING username;";
            await using NpgsqlConnection connection = _database.CreateConnection();
            string? username = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(sql, new { Id = id, Now = (long)UnixTime.Now.TotalMilliseconds }, cancellationToken: cancellationToken));
            if (username is not null) await InvalidateUserAsync(id, username);
        }

        /// <summary>
        /// Changes a user's disabled and admin flags; null leaves a flag as it is.
        /// </summary>
        /// <returns>The updated user, or null when there's no such user.</returns>
        public async Task<VUser?> UpdateUserAsync(string username, bool? disabled, bool? isAdmin, CancellationToken cancellationToken)
        {
            string sql = "UPDATE auth.users SET disabled = COALESCE(@Disabled, disabled), is_admin = COALESCE(@IsAdmin, is_admin) " +
                         $"WHERE lower(username) = lower(@Username) RETURNING {UserColumns};";

            await using NpgsqlConnection connection = _database.CreateConnection();
            VUser? user = await connection.QuerySingleOrDefaultAsync<VUser>(new CommandDefinition(sql, new { Username = username, Disabled = disabled, IsAdmin = isAdmin }, cancellationToken: cancellationToken));
            if (user is not null) await InvalidateUserAsync(user.Id, user.Username);
            return user;
        }

        /// <summary>
        /// Counts admins who can still log in, so the last one can't be disabled or demoted by accident.
        /// </summary>
        public async Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken)
        {
            const string sql = "SELECT COUNT(*)::INTEGER FROM auth.users WHERE is_admin AND NOT disabled;";
            await using NpgsqlConnection connection = _database.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        }

        /// <summary>
        /// Drops a user's cached rows and every list that shows them (users, group members).
        /// </summary>
        private async Task InvalidateUserAsync(Guid id, string username)
        {
            await _dapper.InvalidateAsync(CacheKeys.UserById(id), CacheKeys.UserByName(username));
            await _dapper.BumpAsync(CacheKeys.UserListGeneration, CacheKeys.GroupGeneration);
        }
    }
}
