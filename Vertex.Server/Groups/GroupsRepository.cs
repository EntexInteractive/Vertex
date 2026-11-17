// Copyright 2026 Entex Interactive

using Dapper;
using EntexInteractive.Extensions;
using Npgsql;
using Vertex.Core.Data;
using Vertex.Server.Database;
using Vertex.Server.Users;

namespace Vertex.Server.Groups
{
    public class GroupsRepository
    {
        private const string GroupColumns = "g.id, g.created_at, g.name, (SELECT COUNT(*) FROM auth.group_members m WHERE m.group_id = g.id) AS member_count";

        private readonly PostgresDatabase _database;
        private readonly DapperContext _dapper;

        // Group reads are cached under CacheKeys.GroupGeneration, which every group write bumps: member counts and
        // member lists change with membership, so invalidating single keys would miss them.
        public GroupsRepository(PostgresDatabase database, DapperContext dapper)
        {
            _database = database;
            _dapper = dapper;
        }

        /// <returns>The new group, or null when a user or group already has that name.</returns>
        public async Task<VGroup?> CreateGroupAsync(string name, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (await UsersRepository.LockAndCheckNameTakenAsync(connection, transaction, name, cancellationToken)) return null;

            const string sql = "INSERT INTO auth.groups (id, created_at, name) VALUES (@Id, @CreatedAt, @Name) RETURNING id, created_at, name, 0::BIGINT AS member_count;";
            VGroup group = await connection.QuerySingleAsync<VGroup>(new CommandDefinition(sql, new { Id = Guid.NewGuid(), CreatedAt = (long)UnixTime.Now.TotalMilliseconds, Name = name }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            await _dapper.BumpAsync(CacheKeys.GroupGeneration);
            return group;
        }

        /// <summary>
        /// Deletes a group, its memberships and its project grants.
        /// </summary>
        /// <returns>False when there's no such group.</returns>
        public async Task<bool> DeleteGroupAsync(string name, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            const string sql = "DELETE FROM auth.groups WHERE lower(name) = lower(@Name) RETURNING id;";
            Guid? id = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(sql, new { Name = name }, transaction, cancellationToken: cancellationToken));
            if (id is null) return false;

            // Grants have no foreign key (they can name a user or a group), so remove them here.
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM projects.access WHERE principal_id = @Id;", new { Id = id }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            await _dapper.BumpAsync(CacheKeys.GroupGeneration);
            return true;
        }

        public async Task<IReadOnlyList<VGroup>> ListGroupsAsync(CancellationToken cancellationToken)
        {
            const string sql = $"SELECT {GroupColumns} FROM auth.groups g ORDER BY lower(g.name);";
            string generation = await _dapper.GenerationAsync(CacheKeys.GroupGeneration);
            return await _dapper.QueryAsync<VGroup>(sql, null, CacheKeys.GroupList(generation), cancellationToken: cancellationToken);
        }

        public async Task<VGroup?> GetGroupByNameAsync(string name, CancellationToken cancellationToken)
        {
            const string sql = $"SELECT {GroupColumns} FROM auth.groups g WHERE lower(g.name) = lower(@Name);";
            string generation = await _dapper.GenerationAsync(CacheKeys.GroupGeneration);
            return await _dapper.QuerySingleAsync<VGroup>(sql, new { Name = name }, CacheKeys.GroupByName(generation, name), cancellationToken: cancellationToken);
        }

        public async Task<IReadOnlyList<VUser>> ListMembersAsync(Guid groupId, CancellationToken cancellationToken)
        {
            const string sql = "SELECT u.id, u.created_at, u.username, u.display_name, u.email, u.is_admin, u.disabled, u.last_login_at " +
                               "FROM auth.group_members m JOIN auth.users u ON u.id = m.user_id WHERE m.group_id = @GroupId ORDER BY lower(u.username);";

            string generation = await _dapper.GenerationAsync(CacheKeys.GroupGeneration);
            return await _dapper.QueryAsync<VUser>(sql, new { GroupId = groupId }, CacheKeys.GroupMembers(generation, groupId), cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Finds which of the given usernames don't exist, so a typo is reported instead of silently skipped.
        /// </summary>
        public async Task<IReadOnlyList<string>> FindUnknownUsersAsync(IReadOnlyCollection<string> usernames, CancellationToken cancellationToken)
        {
            const string sql = "SELECT n FROM unnest(@Names) AS n WHERE NOT EXISTS (SELECT 1 FROM auth.users u WHERE lower(u.username) = lower(n));";
            await using NpgsqlConnection connection = _database.CreateConnection();
            return (await connection.QueryAsync<string>(new CommandDefinition(sql, new { Names = usernames.ToArray() }, cancellationToken: cancellationToken))).ToReadOnly();
        }

        /// <summary>
        /// Adds users to a group in one statement. Users who are already members are skipped.
        /// </summary>
        /// <returns>How many users were added.</returns>
        public async Task<int> AddMembersAsync(Guid groupId, IReadOnlyCollection<string> usernames, CancellationToken cancellationToken)
        {
            const string sql = "INSERT INTO auth.group_members (group_id, user_id) " +
                               "SELECT @GroupId, u.id FROM auth.users u WHERE lower(u.username) = ANY (SELECT lower(n) FROM unnest(@Names) AS n) " +
                               "ON CONFLICT DO NOTHING;";

            return await ChangeMembersAsync(sql, groupId, usernames, cancellationToken);
        }

        /// <returns>How many users were removed.</returns>
        public async Task<int> RemoveMembersAsync(Guid groupId, IReadOnlyCollection<string> usernames, CancellationToken cancellationToken)
        {
            const string sql = "DELETE FROM auth.group_members m USING auth.users u " +
                               "WHERE m.group_id = @GroupId AND m.user_id = u.id AND lower(u.username) = ANY (SELECT lower(n) FROM unnest(@Names) AS n);";

            return await ChangeMembersAsync(sql, groupId, usernames, cancellationToken);
        }

        private async Task<int> ChangeMembersAsync(string sql, Guid groupId, IReadOnlyCollection<string> usernames, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            int changed = await connection.ExecuteAsync(new CommandDefinition(sql, new { GroupId = groupId, Names = usernames.ToArray() }, cancellationToken: cancellationToken));
            if (changed > 0) await _dapper.BumpAsync(CacheKeys.GroupGeneration);
            return changed;
        }
    }
}
