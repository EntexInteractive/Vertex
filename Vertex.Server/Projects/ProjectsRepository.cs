// Copyright 2026 Entex Interactive

using System.Data;
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

        public async Task<bool> AddProjectAsync(VProject project, CancellationToken cancellationToken)
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "INSERT INTO projects.projects (id, created_at, name, permission, owner_id) VALUES(@Id, @CreatedAt, @Name, @Permission, @OwnerId);";
            return await _dapper.ExecuteAsync(conn, sql, new { project.Id, CreatedAt = project.CreatedAt.TotalMilliseconds, project.Name, project.Permission, project.OwnerId });
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