// Copyright 2026 Entex Interactive

using System.Data;
using Vertex.Core.Data;
using Vertex.Server.Database;

namespace Vertex.Server.Branches
{
    public class BranchesRepository
    {
        private readonly PostgresDatabase _database;
        private readonly DapperContext _dapper;

        public BranchesRepository(PostgresDatabase database, DapperContext dapper)
        {
            _database = database;
            _dapper = dapper;
        }

        public async Task<bool> AddBranchAsync(VBranch branch, CancellationToken cancellationToken = default)
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "INSERT INTO branches.branches (id, created_at, name, project_id, owner_id, parent_id) VALUES(@Id, @CreatedAt, @Name, @ProjectId, @OwnerId, @ParentId);";
            return await _dapper.ExecuteAsync(conn, sql, new { branch.Id, CreatedAt = branch.CreatedAt.TotalMilliseconds, branch.Name, branch.ProjectId, branch.OwnerId, branch.ParentId });
        }

        public async Task<IReadOnlyList<VBranch>> ListBranchesAsync(Guid projectId, CancellationToken cancellationToken)
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "SELECT id, created_at, name, project_id, owner_id, parent_id FROM branches.branches WHERE project_id = @ProjectId ORDER BY name;";
            return await _dapper.QueryAsync<VBranch>(conn, sql, new { ProjectId = projectId });
        }

        public async Task<VBranch?> GetBranchByIdAsync(Guid projectId, Guid branchId, CancellationToken cancellationToken)
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "SELECT id, created_at, name, project_id, owner_id, parent_id FROM branches.branches WHERE project_id = @ProjectId AND id = @Id;";
            return await _dapper.QuerySingleAsync<VBranch>(conn, sql, new { ProjectId = projectId, Id = branchId });
        }

        public async Task<VBranch?> GetBranchByNameAsync(Guid projectId, string name, CancellationToken cancellationToken)
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "SELECT id, created_at, name, project_id, owner_id, parent_id FROM branches.branches WHERE project_id = @ProjectId AND name = @Name;";
            return await _dapper.QuerySingleAsync<VBranch>(conn, sql, new { ProjectId = projectId, Name = name });
        }
    }
}