// Copyright 2026 Entex Interactive

using System.Data;
using Dapper;
using Npgsql;
using Vertex.Core.Data;
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
        /// Creates a project and its default branch in one transaction, so a project never exists without a branch.
        /// </summary>
        /// <returns>False when a project with the same name already exists; nothing is created.</returns>
        public async Task<bool> CreateProjectAsync(VProject project, VBranch defaultBranch, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                const string projectSql = "INSERT INTO projects.projects (id, created_at, name, permission, owner_id) " +
                                          "VALUES (@Id, @CreatedAt, @Name, @Permission, @OwnerId);";

                await connection.ExecuteAsync(new CommandDefinition(projectSql,
                    new { project.Id, CreatedAt = (long)project.CreatedAt.TotalMilliseconds, project.Name, project.Permission, project.OwnerId },
                    transaction, cancellationToken: cancellationToken));

                const string branchSql = "INSERT INTO branches.branches (id, created_at, name, project_id, owner_id, parent_id) " +
                                         "VALUES (@Id, @CreatedAt, @Name, @ProjectId, @OwnerId, NULL);";

                await connection.ExecuteAsync(new CommandDefinition(branchSql,
                    new { defaultBranch.Id, CreatedAt = (long)defaultBranch.CreatedAt.TotalMilliseconds, defaultBranch.Name, defaultBranch.ProjectId, defaultBranch.OwnerId },
                    transaction, cancellationToken: cancellationToken));

                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<VProject>> ListProjectsAsync(CancellationToken cancellationToken)
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "SELECT id, created_at, name, permission, owner_id FROM projects.projects ORDER BY name;";
            return await _dapper.QueryAsync<VProject>(conn, sql, new { });
        }

        public async Task<VProject?> GetProjectByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "SELECT id, created_at, name, permission, owner_id FROM projects.projects WHERE id = @Id;";
            return await _dapper.QuerySingleAsync<VProject>(conn, sql, new { id });
        }

        public async Task<VProject?> GetProjectByNameAsync(string name, CancellationToken cancellationToken)
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "SELECT id, created_at, name, permission, owner_id FROM projects.projects WHERE name = @Name;";
            return await _dapper.QuerySingleAsync<VProject>(conn, sql, new { Name = name });
        }
    }
}