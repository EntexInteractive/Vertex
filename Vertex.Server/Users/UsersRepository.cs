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

        public UsersRepository(PostgresDatabase database)
        {
            _database = database;
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
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
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

            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                Id = Guid.NewGuid(),
                CreatedAt = (long)UnixTime.Now.TotalMilliseconds,
                Username = username,
                PasswordHash = passwordHash
            }, cancellationToken: cancellationToken)) > 0;
        }

        public async Task<bool> AnyUsersAsync(CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM auth.users);", cancellationToken: cancellationToken));
        }

        public async Task<IReadOnlyList<VUser>> ListUsersAsync(CancellationToken cancellationToken)
        {
            string sql = $"SELECT {UserColumns} FROM auth.users ORDER BY lower(username);";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return (await connection.QueryAsync<VUser>(new CommandDefinition(sql, cancellationToken: cancellationToken))).ToReadOnly();
        }

        public async Task<VUser?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            string sql = $"SELECT {UserColumns} FROM auth.users WHERE id = @Id;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<VUser>(new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
        }

        public async Task<VUser?> GetUserByNameAsync(string username, CancellationToken cancellationToken)
        {
            string sql = $"SELECT {UserColumns} FROM auth.users WHERE lower(username) = lower(@Username);";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<VUser>(new CommandDefinition(sql, new { Username = username }, cancellationToken: cancellationToken));
        }

        public async Task<UserCredentials?> GetCredentialsAsync(string username, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id, password_hash, disabled FROM auth.users WHERE lower(username) = lower(@Username);";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<UserCredentials>(new CommandDefinition(sql, new { Username = username }, cancellationToken: cancellationToken));
        }

        public async Task<string?> GetPasswordHashAsync(Guid id, CancellationToken cancellationToken)
        {
            const string sql = "SELECT password_hash FROM auth.users WHERE id = @Id;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
        }

        public async Task SetPasswordHashAsync(Guid id, string passwordHash, CancellationToken cancellationToken)
        {
            const string sql = "UPDATE auth.users SET password_hash = @PasswordHash WHERE id = @Id;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = id, PasswordHash = passwordHash }, cancellationToken: cancellationToken));
        }

        public async Task SetLastLoginAsync(Guid id, CancellationToken cancellationToken)
        {
            const string sql = "UPDATE auth.users SET last_login_at = @Now WHERE id = @Id;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = id, Now = (long)UnixTime.Now.TotalMilliseconds }, cancellationToken: cancellationToken));
        }

        /// <summary>
        /// Changes a user's disabled and admin flags; null leaves a flag as it is.
        /// </summary>
        /// <returns>The updated user, or null when there's no such user.</returns>
        public async Task<VUser?> UpdateUserAsync(string username, bool? disabled, bool? isAdmin, CancellationToken cancellationToken)
        {
            string sql = "UPDATE auth.users SET disabled = COALESCE(@Disabled, disabled), is_admin = COALESCE(@IsAdmin, is_admin) " +
                         $"WHERE lower(username) = lower(@Username) RETURNING {UserColumns};";

            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<VUser>(new CommandDefinition(sql, new { Username = username, Disabled = disabled, IsAdmin = isAdmin }, cancellationToken: cancellationToken));
        }

        /// <summary>
        /// Counts admins who can still log in, so the last one can't be disabled or demoted by accident.
        /// </summary>
        public async Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken)
        {
            const string sql = "SELECT COUNT(*)::INTEGER FROM auth.users WHERE is_admin AND NOT disabled;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        }
    }
}
