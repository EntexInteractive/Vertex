// Copyright 2026 Entex Interactive

using System.Security.Claims;
using Dapper;
using EntexInteractive.Extensions;
using Npgsql;
using Vertex.Core.Data;
using Vertex.Core.Security;
using Vertex.Server.Auth;
using Vertex.Server.Database;

namespace Vertex.Server.Access
{
    /// <summary>
    /// A user's effective level in a project, and the project's name for messages.
    /// </summary>
    public sealed record AccessDecision(string ProjectName, AccessLevel Level);

    /// <summary>
    /// The outcome of <see cref="AccessService.CheckAsync"/>. When <see cref="Allowed"/> is false, return
    /// <see cref="StatusCode"/> with <see cref="Message"/>.
    /// </summary>
    public sealed record AccessCheck(bool Allowed, int StatusCode, string? Message, AccessDecision? Decision)
    {
        public static AccessCheck Allow(AccessDecision decision)
        {
            return new AccessCheck(true, StatusCodes.Status200OK, null, decision);
        }
    }

    /// <summary>
    /// Project permissions: fixed levels (read, write, manage) granted to users or groups, plus a server-admin flag
    /// that gives admin in every project. There are no custom roles and no default access: without a grant, a user
    /// can't see a project.
    /// </summary>
    /// <remarks>
    /// A user's effective level is the highest of: admin when they are a server admin, their own grant, and the grants
    /// of every group they belong to. It's one query, cached per user and
    /// project. Cache keys include a generation per project and a global one; changing a project's grants starts a
    /// new project generation, and changing group membership or user flags starts a new global one, so no stale level
    /// survives a change.
    /// </remarks>
    public class AccessService
    {
        private const double LevelCacheMinutes = 5;

        private readonly PostgresDatabase _database;
        private readonly CacheContext _cache;

        public AccessService(PostgresDatabase database, CacheContext cache)
        {
            _database = database;
            _cache = cache;
        }

        /// <summary>
        /// Checks that the caller has at least <paramref name="required"/> in a project.
        /// </summary>
        /// <returns>Allowed, or 404 when the project doesn't exist, or 403 with a message naming the fix.</returns>
        public async Task<AccessCheck> CheckAsync(ClaimsPrincipal user, Guid projectId, AccessLevel required, CancellationToken cancellationToken)
        {
            AccessDecision? decision = await GetDecisionAsync(user.GetUserId(), projectId, cancellationToken);
            if (decision is null) return new AccessCheck(false, StatusCodes.Status404NotFound, "The project does not exist.", null);
            if (decision.Level >= required) return AccessCheck.Allow(decision);

            string level = required.ToString().ToLowerInvariant();
            string message = $"You need {level} access to '{decision.ProjectName}'. Ask someone with manage access to run 'vx access grant {user.GetUsername()} {level}'.";
            return new AccessCheck(false, StatusCodes.Status403Forbidden, message, decision);
        }

        /// <summary>
        /// The user's effective level in a project.
        /// </summary>
        /// <returns>Null when the project doesn't exist.</returns>
        public async Task<AccessDecision?> GetDecisionAsync(Guid userId, Guid projectId, CancellationToken cancellationToken)
        {
            string global = await _cache.GetGenerationAsync(CacheKeys.AccessGeneration);
            string project = await _cache.GetGenerationAsync(CacheKeys.ProjectAccessGeneration(projectId));
            string key = CacheKeys.Access(global, project, projectId, userId);

            return await _cache.GetOrSetAsync(key, async ct =>
            {
                const string sql = "SELECT p.name AS project_name, GREATEST(" +
                                   "CASE WHEN u.is_admin THEN 4 ELSE 0 END, " +
                                   "COALESCE((SELECT MAX(a.level) FROM projects.access a WHERE a.project_id = p.id AND " +
                                   "(a.principal_id = u.id OR a.principal_id IN (SELECT m.group_id FROM auth.group_members m WHERE m.user_id = u.id))), 0)" +
                                   ")::SMALLINT AS level " +
                                   "FROM projects.projects p CROSS JOIN auth.users u WHERE p.id = @ProjectId AND u.id = @UserId;";

                await using NpgsqlConnection connection = _database.CreateConnection();
                return await connection.QuerySingleOrDefaultAsync<AccessDecision>(new CommandDefinition(sql, new { ProjectId = projectId, UserId = userId }, cancellationToken: ct));
            }, LevelCacheMinutes);
        }

        public async Task<IReadOnlyList<VAccessGrant>> ListGrantsAsync(Guid projectId, CancellationToken cancellationToken)
        {
            const string sql = "SELECT a.principal_id, a.principal_kind AS kind, COALESCE(u.username, g.name) AS name, a.level " +
                               "FROM projects.access a " +
                               "LEFT JOIN auth.users u ON a.principal_kind = 0 AND u.id = a.principal_id " +
                               "LEFT JOIN auth.groups g ON a.principal_kind = 1 AND g.id = a.principal_id " +
                               "WHERE a.project_id = @ProjectId ORDER BY a.level DESC, lower(COALESCE(u.username, g.name));";

            // Under the global generation too: deleting a group removes its grants in every project.
            string global = await _cache.GetGenerationAsync(CacheKeys.AccessGeneration);
            string project = await _cache.GetGenerationAsync(CacheKeys.ProjectAccessGeneration(projectId));
            return await _cache.GetOrSetAsync<IReadOnlyList<VAccessGrant>>(CacheKeys.Grants(global, project, projectId), async ct =>
            {
                await using NpgsqlConnection connection = _database.CreateConnection();
                return (await connection.QueryAsync<VAccessGrant>(new CommandDefinition(sql, new { ProjectId = projectId }, cancellationToken: ct))).ToReadOnly();
            }, LevelCacheMinutes, cancellationToken) ?? [];
        }

        /// <summary>
        /// Gives a user or group a level in a project, replacing any level it had.
        /// </summary>
        /// <returns>The grant, or null when no user or group has that name.</returns>
        public async Task<VAccessGrant?> GrantAsync(Guid projectId, string name, AccessLevel level, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            VAccessGrant? principal = await FindPrincipalAsync(connection, transaction, name, cancellationToken);
            if (principal is null) return null;

            const string sql = "INSERT INTO projects.access (project_id, principal_id, principal_kind, level) VALUES (@ProjectId, @PrincipalId, @Kind, @Level) " +
                               "ON CONFLICT (project_id, principal_id) DO UPDATE SET level = EXCLUDED.level;";

            await connection.ExecuteAsync(new CommandDefinition(sql, new { ProjectId = projectId, principal.PrincipalId, Kind = (short)principal.Kind, Level = (short)level }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            await InvalidateProjectAsync(projectId);
            return principal with { Level = level };
        }

        /// <summary>
        /// Adds a grant inside an existing transaction, e.g. giving a project's creator manage access.
        /// </summary>
        public static Task GrantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, Guid principalId, PrincipalKind kind, AccessLevel level, CancellationToken cancellationToken)
        {
            const string sql = "INSERT INTO projects.access (project_id, principal_id, principal_kind, level) VALUES (@ProjectId, @PrincipalId, @Kind, @Level) " +
                               "ON CONFLICT (project_id, principal_id) DO UPDATE SET level = EXCLUDED.level;";

            return connection.ExecuteAsync(new CommandDefinition(sql, new { ProjectId = projectId, PrincipalId = principalId, Kind = (short)kind, Level = (short)level }, transaction, cancellationToken: cancellationToken));
        }

        /// <summary>
        /// Removes a user's or group's grant from a project.
        /// </summary>
        /// <returns>False when the name had no grant in the project.</returns>
        public async Task<bool> RevokeAsync(Guid projectId, string name, CancellationToken cancellationToken)
        {
            const string sql = "DELETE FROM projects.access WHERE project_id = @ProjectId AND principal_id IN (" +
                               "SELECT id FROM auth.users WHERE lower(username) = lower(@Name) " +
                               "UNION ALL SELECT id FROM auth.groups WHERE lower(name) = lower(@Name));";

            await using NpgsqlConnection connection = _database.CreateConnection();
            bool removed = await connection.ExecuteAsync(new CommandDefinition(sql, new { ProjectId = projectId, Name = name }, cancellationToken: cancellationToken)) > 0;
            if (removed) await InvalidateProjectAsync(projectId);
            return removed;
        }

        /// <summary>
        /// Drops every cached level and project list. Call after changes that can affect any project: group
        /// membership, a user's admin flag, deleting a group.
        /// </summary>
        public Task InvalidateAllAsync()
        {
            return _cache.BumpGenerationAsync(CacheKeys.AccessGeneration, CacheKeys.ProjectListGeneration);
        }

        /// <summary>
        /// Drops a project's cached levels and grants, and every user's project list (the project may have appeared
        /// in or disappeared from them).
        /// </summary>
        private Task InvalidateProjectAsync(Guid projectId)
        {
            return _cache.BumpGenerationAsync(CacheKeys.ProjectAccessGeneration(projectId), CacheKeys.ProjectListGeneration);
        }

        private static async Task<VAccessGrant?> FindPrincipalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id AS principal_id, 0::SMALLINT AS kind, username AS name, 0::SMALLINT AS level FROM auth.users WHERE lower(username) = lower(@Name) " +
                               "UNION ALL SELECT id, 1::SMALLINT, name, 0::SMALLINT FROM auth.groups WHERE lower(name) = lower(@Name);";

            return await connection.QueryFirstOrDefaultAsync<VAccessGrant>(new CommandDefinition(sql, new { Name = name }, transaction, cancellationToken: cancellationToken));
        }
    }
}
