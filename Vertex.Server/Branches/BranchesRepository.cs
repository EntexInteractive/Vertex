// Copyright 2026 Entex Interactive

using Dapper;
using Npgsql;
using Vertex.Core.Data;
using Vertex.Server.Database;

namespace Vertex.Server.Branches
{
    public class BranchesRepository
    {
        /// <summary>Every column of a branch, in <see cref="VBranch"/>'s constructor order.</summary>
        private const string Columns = "id, created_at, name, project_id, parent_id, base_revision, merge_to";

        /// <summary>Locks the project's counters row, so branch changes serialize with submits and each other.</summary>
        private const string LockSql = "SELECT 1 FROM projects.counters WHERE project_id = @ProjectId FOR UPDATE;";

        /// <summary>The longest chain of parents or merge targets the checks walk; deeper ones are treated as a loop.</summary>
        private const int MaximumDepth = 64;

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
        /// <see cref="VBranch.BaseRevision"/>. Without a parent the branch starts empty. With
        /// <see cref="VBranch.MergeTo"/> set, its revisions are merged on into that branch.
        /// </summary>
        /// <returns>False when the project already has a branch with that name.</returns>
        /// <exception cref="ArgumentException">The parent or merge target isn't a branch of the project, or the target
        /// shares no history with the new branch.</exception>
        public async Task<bool> AddBranchAsync(VBranch branch, CancellationToken cancellationToken = default)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            // Lock the project's counters row like a submit does, so no revision lands on the parent between reading
            // its head and copying its files, and no other branch change races the merge-target checks.
            if (branch.ParentId is not null || branch.MergeTo is not null)
                await connection.ExecuteAsync(new CommandDefinition(LockSql, new { branch.ProjectId }, transaction, cancellationToken: cancellationToken));

            long baseRevision = 0;
            if (branch.ParentId is Guid parentId)
            {
                const string headSql = "SELECT GREATEST(b.base_revision, COALESCE((SELECT MAX(r.number) FROM revisions.revisions r WHERE r.branch_id = b.id), 0)) " +
                                       "FROM branches.branches b WHERE b.id = @ParentId AND b.project_id = @ProjectId;";
                baseRevision = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(headSql, new { ParentId = parentId, branch.ProjectId }, transaction, cancellationToken: cancellationToken))
                               ?? throw new ArgumentException("The parent branch doesn't exist in this project.", nameof(branch));
            }

            if (branch.MergeTo is Guid mergeTo && !await ExistsAsync(connection, transaction, branch.ProjectId, mergeTo, cancellationToken))
                throw new ArgumentException("The branch to merge into doesn't exist in this project.", nameof(branch));

            const string insertSql = "INSERT INTO branches.branches (id, created_at, name, project_id, parent_id, base_revision, merge_to) " +
                                     "VALUES(@Id, @CreatedAt, @Name, @ProjectId, @ParentId, @BaseRevision, @MergeTo) ON CONFLICT ON CONSTRAINT uq_branches_project_name DO NOTHING;";
            int inserted = await connection.ExecuteAsync(new CommandDefinition(insertSql,
                new { branch.Id, CreatedAt = (long)branch.CreatedAt.TotalMilliseconds, branch.Name, branch.ProjectId, branch.ParentId, BaseRevision = baseRevision, branch.MergeTo },
                transaction, cancellationToken: cancellationToken));
            if (inserted == 0) return false;

            // Nothing merges into a new branch yet, so its target can't make a loop; it only has to share history.
            if (branch.MergeTo is Guid target && !await RelatedAsync(connection, transaction, branch.Id, target, cancellationToken))
                throw new ArgumentException("The branch to merge into shares no history with this one, so it can't be merged into.", nameof(branch));

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
        /// Sets (or with null clears) the branch a branch's revisions are merged into automatically.
        /// </summary>
        /// <returns>The branch and its previous target, or null when the branch doesn't exist.</returns>
        /// <exception cref="ArgumentException">The target isn't a branch of the project, is the branch itself, shares no
        /// history with it, or already merges (directly or along its chain) into this branch.</exception>
        public async Task<MergeToChange?> SetMergeToAsync(Guid projectId, Guid branchId, Guid? mergeTo, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(LockSql, new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));

            VBranch? branch = await connection.QuerySingleOrDefaultAsync<VBranch>(new CommandDefinition(
                $"SELECT {Columns} FROM branches.branches WHERE project_id = @ProjectId AND id = @Id;", new { ProjectId = projectId, Id = branchId }, transaction,
                cancellationToken: cancellationToken));
            if (branch is null) return null;

            Guid? previous = branch.MergeTo;
            if (previous == mergeTo) return new MergeToChange(branch, previous);

            if (mergeTo is Guid target)
            {
                if (target == branchId) throw new ArgumentException($"'{branch.Name}' can't merge into itself.");
                if (!await ExistsAsync(connection, transaction, projectId, target, cancellationToken))
                    throw new ArgumentException("The branch to merge into doesn't exist in this project.");
                if (!await RelatedAsync(connection, transaction, branchId, target, cancellationToken))
                    throw new ArgumentException($"That branch shares no history with '{branch.Name}', so it can't be merged into.");

                // Following the target's own chain must never come back here, or a revision would merge in a circle.
                const string loopSql = "WITH RECURSIVE c AS (" +
                                       "  SELECT id, merge_to, 0 AS depth FROM branches.branches WHERE id = @Target " +
                                       "  UNION ALL " +
                                       "  SELECT b.id, b.merge_to, c.depth + 1 FROM c JOIN branches.branches b ON b.id = c.merge_to WHERE c.depth < @Depth" +
                                       ") SELECT EXISTS (SELECT 1 FROM c WHERE id = @Branch) OR (SELECT MAX(depth) FROM c) >= @Depth;";
                if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(loopSql, new { Target = target, Branch = branchId, Depth = MaximumDepth }, transaction,
                        cancellationToken: cancellationToken)))
                    throw new ArgumentException($"That branch already merges into '{branch.Name}' (directly or through others), so this would merge in a circle.");
            }

            await connection.ExecuteAsync(new CommandDefinition("UPDATE branches.branches SET merge_to = @MergeTo WHERE id = @Id;", new { MergeTo = mergeTo, Id = branchId }, transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            branch.MergeTo = mergeTo;

            await _dapper.InvalidateAsync(CacheKeys.BranchById(projectId, branchId), CacheKeys.BranchByName(projectId, branch.Name), CacheKeys.BranchList(projectId));
            return new MergeToChange(branch, previous);
        }

        /// <summary>
        /// Deletes a branch by name.
        /// </summary>
        /// <returns>The deleted branch, or null when it doesn't exist.</returns>
        /// <exception cref="InvalidOperationException">Other branches were created from it, or merge into it.</exception>
        public async Task<VBranch?> DeleteBranchAsync(Guid projectId, string name, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            string sql = $"DELETE FROM branches.branches WHERE project_id = @ProjectId AND name = @Name RETURNING {Columns};";

            VBranch? branch;
            try
            {
                branch = await connection.QuerySingleOrDefaultAsync<VBranch>(new CommandDefinition(sql, new { ProjectId = projectId, Name = name }, cancellationToken: cancellationToken));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation && ex.ConstraintName == "fk_branches_merge_to")
            {
                const string sourceSql = "SELECT s.name FROM branches.branches s JOIN branches.branches t ON t.id = s.merge_to " +
                                         "WHERE t.project_id = @ProjectId AND t.name = @Name ORDER BY s.name LIMIT 1;";
                string source = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(sourceSql, new { ProjectId = projectId, Name = name }, cancellationToken: cancellationToken))
                                ?? "Another branch";
                throw new InvalidOperationException($"'{source}' auto-merges into '{name}'. Run 'vx branch {source} --merge-to none' first.", ex);
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
            const string sql = $"SELECT {Columns} FROM branches.branches WHERE project_id = @ProjectId ORDER BY name;";
            return await _dapper.QueryAsync<VBranch>(sql, new { ProjectId = projectId }, CacheKeys.BranchList(projectId), cancellationToken: cancellationToken);
        }

        public async Task<VBranch?> GetBranchByIdAsync(Guid projectId, Guid branchId, CancellationToken cancellationToken)
        {
            const string sql = $"SELECT {Columns} FROM branches.branches WHERE project_id = @ProjectId AND id = @Id;";
            return await _dapper.QuerySingleAsync<VBranch>(sql, new { ProjectId = projectId, Id = branchId }, CacheKeys.BranchById(projectId, branchId), cancellationToken: cancellationToken);
        }

        public async Task<VBranch?> GetBranchByNameAsync(Guid projectId, string name, CancellationToken cancellationToken)
        {
            const string sql = $"SELECT {Columns} FROM branches.branches WHERE project_id = @ProjectId AND name = @Name;";
            return await _dapper.QuerySingleAsync<VBranch>(sql, new { ProjectId = projectId, Name = name }, CacheKeys.BranchByName(projectId, name), cancellationToken: cancellationToken);
        }

        private static Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, Guid branchId, CancellationToken cancellationToken)
        {
            return connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM branches.branches WHERE id = @Id AND project_id = @ProjectId);",
                new { Id = branchId, ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        }

        /// <summary>
        /// True when the two branches have a branch in common along their chains of parents (one is the other's
        /// ancestor, or both were cut from the same one), so a merge between them has a base.
        /// </summary>
        private static Task<bool> RelatedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid a, Guid b, CancellationToken cancellationToken)
        {
            const string sql = "WITH RECURSIVE x AS (" +
                               "  SELECT id, parent_id, 0 AS depth FROM branches.branches WHERE id = @A " +
                               "  UNION ALL SELECT p.id, p.parent_id, x.depth + 1 FROM x JOIN branches.branches p ON p.id = x.parent_id WHERE x.depth < @Depth" +
                               "), y AS (" +
                               "  SELECT id, parent_id, 0 AS depth FROM branches.branches WHERE id = @B " +
                               "  UNION ALL SELECT p.id, p.parent_id, y.depth + 1 FROM y JOIN branches.branches p ON p.id = y.parent_id WHERE y.depth < @Depth" +
                               ") SELECT EXISTS (SELECT 1 FROM x JOIN y ON y.id = x.id);";
            return connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { A = a, B = b, Depth = MaximumDepth }, transaction, cancellationToken: cancellationToken));
        }
    }
}
