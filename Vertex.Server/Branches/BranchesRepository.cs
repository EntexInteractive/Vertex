// Copyright 2026 Entex Interactive

using Dapper;
using Npgsql;
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
            const string sql = "INSERT INTO branches.branches (id, created_at, name, project_id, parent_id) VALUES(@Id, @CreatedAt, @Name, @ProjectId, @ParentId);";

            // A lookup before the branch existed may have cached "not found".
            return await _dapper.ExecuteAsync(sql, new { branch.Id, CreatedAt = (long)branch.CreatedAt.TotalMilliseconds, branch.Name, branch.ProjectId, branch.ParentId },
                cancellationToken, CacheKeys.BranchByName(branch.ProjectId, branch.Name), CacheKeys.BranchList(branch.ProjectId));
        }

        /// <summary>
        /// Deletes a branch by name.
        /// </summary>
        /// <returns>The deleted branch, or null when it doesn't exist.</returns>
        /// <exception cref="InvalidOperationException">Other branches were created from it.</exception>
        public async Task<VBranch?> DeleteBranchAsync(Guid projectId, string name, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            const string sql = "DELETE FROM branches.branches WHERE project_id = @ProjectId AND name = @Name " +
                               "RETURNING id, created_at, name, project_id, parent_id;";

            VBranch? branch;
            try
            {
                branch = await connection.QuerySingleOrDefaultAsync<VBranch>(new CommandDefinition(sql, new { ProjectId = projectId, Name = name }, cancellationToken: cancellationToken));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new InvalidOperationException($"Branch '{name}' has child branches.", ex);
            }

            // Includes the existence check every revision and lock call makes, so workspaces on it fail at once.
            if (branch is not null)
            {
                await _dapper.InvalidateAsync(CacheKeys.BranchById(projectId, branch.Id), CacheKeys.BranchByName(projectId, branch.Name),
                    CacheKeys.BranchList(projectId), CacheKeys.BranchExists(projectId, branch.Id));
            }

            return branch;
        }

        public async Task<IReadOnlyList<VBranch>> ListBranchesAsync(Guid projectId, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id, created_at, name, project_id, parent_id FROM branches.branches WHERE project_id = @ProjectId ORDER BY name;";
            return await _dapper.QueryAsync<VBranch>(sql, new { ProjectId = projectId }, CacheKeys.BranchList(projectId), cancellationToken: cancellationToken);
        }

        public async Task<VBranch?> GetBranchByIdAsync(Guid projectId, Guid branchId, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id, created_at, name, project_id, parent_id FROM branches.branches WHERE project_id = @ProjectId AND id = @Id;";
            return await _dapper.QuerySingleAsync<VBranch>(sql, new { ProjectId = projectId, Id = branchId }, CacheKeys.BranchById(projectId, branchId), cancellationToken: cancellationToken);
        }

        public async Task<VBranch?> GetBranchByNameAsync(Guid projectId, string name, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id, created_at, name, project_id, parent_id FROM branches.branches WHERE project_id = @ProjectId AND name = @Name;";
            return await _dapper.QuerySingleAsync<VBranch>(sql, new { ProjectId = projectId, Name = name }, CacheKeys.BranchByName(projectId, name), cancellationToken: cancellationToken);
        }
    }
}
