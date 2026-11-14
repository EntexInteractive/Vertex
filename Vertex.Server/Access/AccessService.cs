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
    /// Project permissions: fixed levels (read, write, admin) granted to users or groups, plus a server-admin flag
    /// and per-project visibility. There are no custom roles.
    /// </summary>
    /// <remarks>
    /// A user's effective level is the highest of: admin when they are a server admin, read when the project is
    /// internal, their own grant, and the grants of every group they belong to. It's one query, cached per user and
    /// project. Cache keys include a generation per project and a global one; changing a project's grants starts a
    /// new project generation, and changing group membership or user flags starts a new global one, so no stale level
    /// survives a change.
    /// </remarks>
    public class AccessService
    {
        private const double LevelCacheMinutes = 5;
        private const double GenerationCacheMinutes = 24 * 60;
        private const string GlobalGenerationKey = "access:gen";

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
            string message = $"You need {level} access to '{decision.ProjectName}'. Ask a project admin to run 'vx access grant {user.GetUsername()} {level}'.";
            return new AccessCheck(false, StatusCodes.Status403Forbidden, message, decision);
        }

        /// <summary>
        /// The user's effective level in a project.
        /// </summary>
        /// <returns>Null when the project doesn't exist.</returns>
        public async Task<AccessDecision?> GetDecisionAsync(Guid userId, Guid projectId, CancellationToken cancellationToken)
        {
            string global = await GetGenerationAsync(GlobalGenerationKey);
            string project = await GetGenerationAsync(ProjectGenerationKey(projectId));
            string key = $"access:{global}:{project}:{projectId:N}:{userId:N}";

            return await _cache.GetOrSetAsync(key, async ct =>
            {
                const string sql = "SELECT p.name AS project_name, GREATEST(" +
                                   "CASE WHEN u.is_admin THEN 3 ELSE 0 END, " +
                                   "CASE WHEN p.visibility = 1 THEN 1 ELSE 0 END, " +
                                   "COALESCE((SELECT MAX(a.level) FROM projects.access a WHERE a.project_id = p.id AND " +
                                   "(a.principal_id = u.id OR a.principal_id IN (SELECT m.group_id FROM auth.group_members m WHERE m.user_id = u.id))), 0)" +
                                   ")::SMALLINT AS level " +
                                   "FROM projects.projects p CROSS JOIN auth.users u WHERE p.id = @ProjectId AND u.id = @UserId;";

                await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
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

            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return (await connection.QueryAsync<VAccessGrant>(new CommandDefinition(sql, new { ProjectId = projectId }, cancellationToken: cancellationToken))).ToReadOnly();
        }

        /// <summary>
        /// Gives a user or group a level in a project, replacing any level it had.
        /// </summary>
        /// <returns>The grant, or null when no user or group has that name.</returns>
        public async Task<VAccessGrant?> GrantAsync(Guid projectId, string name, AccessLevel level, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
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
        /// Adds a grant inside an existing transaction, e.g. making a project's creator its admin.
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

            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            bool removed = await connection.ExecuteAsync(new CommandDefinition(sql, new { ProjectId = projectId, Name = name }, cancellationToken: cancellationToken)) > 0;
            if (removed) await InvalidateProjectAsync(projectId);
            return removed;
        }

        /// <returns>The updated project, or null when it doesn't exist.</returns>
        public async Task<VProject?> SetVisibilityAsync(Guid projectId, ProjectVisibility visibility, CancellationToken cancellationToken)
        {
            const string sql = "UPDATE projects.projects SET visibility = @Visibility WHERE id = @Id RETURNING id, created_at, name, visibility, owner_id;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            VProject? project = await connection.QuerySingleOrDefaultAsync<VProject>(new CommandDefinition(sql, new { Id = projectId, Visibility = (short)visibility }, cancellationToken: cancellationToken));
            if (project is not null) await InvalidateProjectAsync(projectId);
            return project;
        }

        /// <summary>
        /// Drops every cached level. Call after changes that can affect any project: group membership, a user's
        /// admin flag, deleting a group.
        /// </summary>
        public Task InvalidateAllAsync()
        {
            return _cache.DeleteCachedValueAsync(GlobalGenerationKey);
        }

        private Task InvalidateProjectAsync(Guid projectId)
        {
            return _cache.DeleteCachedValueAsync(ProjectGenerationKey(projectId));
        }

        private async Task<string> GetGenerationAsync(string key)
        {
            return await _cache.GetOrSetAsync(key, _ => ValueTask.FromResult(Guid.NewGuid().ToString("N")), GenerationCacheMinutes) ?? string.Empty;
        }

        private static string ProjectGenerationKey(Guid projectId)
        {
            return $"access:gen:{projectId:N}";
        }

        private static async Task<VAccessGrant?> FindPrincipalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id AS principal_id, 0::SMALLINT AS kind, username AS name, 0::SMALLINT AS level FROM auth.users WHERE lower(username) = lower(@Name) " +
                               "UNION ALL SELECT id, 1::SMALLINT, name, 0::SMALLINT FROM auth.groups WHERE lower(name) = lower(@Name);";

            return await connection.QueryFirstOrDefaultAsync<VAccessGrant>(new CommandDefinition(sql, new { Name = name }, transaction, cancellationToken: cancellationToken));
        }
    }
}
