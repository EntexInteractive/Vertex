// Copyright 2026 Entex Interactive

using Dapper;
using Npgsql;
using Vertex.Core.Data;
using Vertex.Core.Security;
using Vertex.Server.Access;
using Vertex.Server.Database;

namespace Vertex.Server.Projects
{
    public class ProjectsRepository
    {
        private readonly PostgresDatabase _database;
        private readonly DapperContext _dapper;

        public ProjectsRepository(PostgresDatabase database, DapperContext dapper)
        {
            _database = database;
            _dapper = dapper;
        }

        /// <summary>
        /// Creates a project, its default branch and its owner's admin grant in one transaction, so a project never
        /// exists without a branch or an admin.
        /// </summary>
        /// <returns>False when a project with the same name already exists; nothing is created.</returns>
        public async Task<bool> CreateProjectAsync(VProject project, VBranch defaultBranch, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                const string projectSql = "INSERT INTO projects.projects (id, created_at, name, owner_id) " +
                                          "VALUES (@Id, @CreatedAt, @Name, @OwnerId);";

                await connection.ExecuteAsync(new CommandDefinition(projectSql,
                    new { project.Id, CreatedAt = (long)project.CreatedAt.TotalMilliseconds, project.Name, project.OwnerId },
                    transaction, cancellationToken: cancellationToken));

                // The row every write to the project locks and numbers its revisions and events from.
                await connection.ExecuteAsync(new CommandDefinition("INSERT INTO projects.counters (project_id) VALUES (@Id);", new { project.Id },
                    transaction, cancellationToken: cancellationToken));

                const string branchSql = "INSERT INTO branches.branches (id, created_at, name, project_id, parent_id) " +
                                         "VALUES (@Id, @CreatedAt, @Name, @ProjectId, NULL);";

                await connection.ExecuteAsync(new CommandDefinition(branchSql,
                    new { defaultBranch.Id, CreatedAt = (long)defaultBranch.CreatedAt.TotalMilliseconds, defaultBranch.Name, defaultBranch.ProjectId },
                    transaction, cancellationToken: cancellationToken));

                await AccessService.GrantAsync(connection, transaction, project.Id, project.OwnerId, PrincipalKind.User, AccessLevel.Manage, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                // A lookup before the project existed may have cached "not found".
                await _dapper.InvalidateAsync(CacheKeys.ProjectByName(project.Name));
                await _dapper.BumpAsync(CacheKeys.ProjectListGeneration);
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return false;
            }
        }

        /// <summary>
        /// Lists the projects a user can read: all of them for a server admin, otherwise the projects granted
        /// to the user or one of their groups.
        /// </summary>
        /// <remarks>
        /// Cached per user under <see cref="CacheKeys.ProjectListGeneration"/>, which every change to who can see what
        /// bumps: new projects, grants, group membership and admin flags.
        /// </remarks>
        public async Task<IReadOnlyList<VProject>> ListProjectsForUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            const string sql = "SELECT p.id, p.created_at, p.name, p.owner_id FROM projects.projects p " +
                               "WHERE EXISTS (SELECT 1 FROM auth.users u WHERE u.id = @UserId AND u.is_admin) " +
                               "OR EXISTS (SELECT 1 FROM projects.access a WHERE a.project_id = p.id AND (a.principal_id = @UserId " +
                               "OR a.principal_id IN (SELECT m.group_id FROM auth.group_members m WHERE m.user_id = @UserId))) " +
                               "ORDER BY p.name;";

            string generation = await _dapper.GenerationAsync(CacheKeys.ProjectListGeneration);
            return await _dapper.QueryAsync<VProject>(sql, new { UserId = userId }, CacheKeys.ProjectList(generation, userId), cancellationToken: cancellationToken);
        }

        public async Task<VProject?> GetProjectByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id, created_at, name, owner_id FROM projects.projects WHERE id = @Id;";
            return await _dapper.QuerySingleAsync<VProject>(sql, new { Id = id }, CacheKeys.ProjectById(id), cancellationToken: cancellationToken);
        }

        public async Task<VProject?> GetProjectByNameAsync(string name, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id, created_at, name, owner_id FROM projects.projects WHERE name = @Name;";
            return await _dapper.QuerySingleAsync<VProject>(sql, new { Name = name }, CacheKeys.ProjectByName(name), cancellationToken: cancellationToken);
        }
    }
}
