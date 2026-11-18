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

        /// <summary>
        /// Creates a branch. A branch with a parent starts with the parent's files as of its latest revision: the
        /// parent's file list is copied (metadata only, never content) and that revision is kept as
        /// <see cref="VBranch.BaseRevision"/>. Without a parent the branch starts empty.
        /// </summary>
        /// <returns>False when the project already has a branch with that name.</returns>
        /// <exception cref="ArgumentException">The parent isn't a branch of the project.</exception>
        public async Task<bool> AddBranchAsync(VBranch branch, CancellationToken cancellationToken = default)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            long baseRevision = 0;
            if (branch.ParentId is Guid parentId)
            {
                // Lock the project's counters row like a submit does, so no revision lands on the parent between
                // reading its head and copying its files.
                const string lockSql = "SELECT 1 FROM projects.counters WHERE project_id = @ProjectId FOR UPDATE;";
                await connection.ExecuteAsync(new CommandDefinition(lockSql, new { branch.ProjectId }, transaction, cancellationToken: cancellationToken));

                const string headSql = "SELECT GREATEST(b.base_revision, COALESCE((SELECT MAX(r.number) FROM revisions.revisions r WHERE r.branch_id = b.id), 0)) " +
                                       "FROM branches.branches b WHERE b.id = @ParentId AND b.project_id = @ProjectId;";
                baseRevision = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(headSql, new { ParentId = parentId, branch.ProjectId }, transaction, cancellationToken: cancellationToken))
                               ?? throw new ArgumentException("The parent branch doesn't exist in this project.", nameof(branch));
            }

            const string insertSql = "INSERT INTO branches.branches (id, created_at, name, project_id, parent_id, base_revision) " +
                                     "VALUES(@Id, @CreatedAt, @Name, @ProjectId, @ParentId, @BaseRevision) ON CONFLICT ON CONSTRAINT uq_branches_project_name DO NOTHING;";
            int inserted = await connection.ExecuteAsync(new CommandDefinition(insertSql,
                new { branch.Id, CreatedAt = (long)branch.CreatedAt.TotalMilliseconds, branch.Name, branch.ProjectId, branch.ParentId, BaseRevision = baseRevision },
                transaction, cancellationToken: cancellationToken));
            if (inserted == 0) return false;

            if (branch.ParentId is Guid parent)
            {
                const string copySql = "INSERT INTO revisions.head (branch_id, path_key, path, file_id, revision) " +
                                       "SELECT @BranchId, path_key, path, file_id, revision FROM revisions.head WHERE branch_id = @ParentId;";
                await connection.ExecuteAsync(new CommandDefinition(copySql, new { BranchId = branch.Id, ParentId = parent }, transaction, commandTimeout: 300, cancellationToken: cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
            branch.BaseRevision = baseRevision;

            // A lookup before the branch existed may have cached "not found".
            await _dapper.InvalidateAsync(CacheKeys.BranchByName(branch.ProjectId, branch.Name), CacheKeys.BranchList(branch.ProjectId));
            return true;
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
                               "RETURNING id, created_at, name, project_id, parent_id, base_revision;";

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
            const string sql = "SELECT id, created_at, name, project_id, parent_id, base_revision FROM branches.branches WHERE project_id = @ProjectId ORDER BY name;";
            return await _dapper.QueryAsync<VBranch>(sql, new { ProjectId = projectId }, CacheKeys.BranchList(projectId), cancellationToken: cancellationToken);
        }

        public async Task<VBranch?> GetBranchByIdAsync(Guid projectId, Guid branchId, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id, created_at, name, project_id, parent_id, base_revision FROM branches.branches WHERE project_id = @ProjectId AND id = @Id;";
            return await _dapper.QuerySingleAsync<VBranch>(sql, new { ProjectId = projectId, Id = branchId }, CacheKeys.BranchById(projectId, branchId), cancellationToken: cancellationToken);
        }

        public async Task<VBranch?> GetBranchByNameAsync(Guid projectId, string name, CancellationToken cancellationToken)
        {
            const string sql = "SELECT id, created_at, name, project_id, parent_id, base_revision FROM branches.branches WHERE project_id = @ProjectId AND name = @Name;";
            return await _dapper.QuerySingleAsync<VBranch>(sql, new { ProjectId = projectId, Name = name }, CacheKeys.BranchByName(projectId, name), cancellationToken: cancellationToken);
        }
    }
}
