// Copyright 2026 Entex Interactive

using Dapper;
using Npgsql;
using Vertex.Server.Database;
using Vertex.Server.Events;

namespace Vertex.Server.History
{
    /// <summary>
    /// One file in a submit. <see cref="FileId"/> is null to delete the file; <see cref="BaseFileId"/> is null for a
    /// file that is new to the workspace.
    /// </summary>
    public sealed record SubmitChange(string Path, string PathKey, string? FileId, string? BaseFileId);

    /// <summary>
    /// The result of a submit: the new revision number, or why nothing was written: the paths that changed on the
    /// branch since the submitter's base, or the locks someone else holds on submitted paths.
    /// </summary>
    /// <param name="EventSeq">The project's event seq after the submit's events, or null when nothing was written.</param>
    public sealed record SubmitOutcome(long? Revision, IReadOnlyList<string> OutOfDate, IReadOnlyList<LockRow> LockedByOthers, long? EventSeq = null);

    /// <summary>
    /// A file on a branch, for sync. <see cref="FileId"/> is null when the file was deleted.
    /// </summary>
    public sealed record HeadFileRow(string PathKey, string Path, string? FileId, long Revision);

    public class RevisionsRepository
    {
        private readonly PostgresDatabase _database;
        private readonly CacheContext _cache;

        public RevisionsRepository(PostgresDatabase database, CacheContext cache)
        {
            _database = database;
            _cache = cache;
        }

        /// <summary>
        /// True when the branch exists in the project. Cached for a minute, because every revision and lock call
        /// checks it, including the lock poll idle clients make constantly.
        /// </summary>
        public async Task<bool> BranchExistsAsync(Guid projectId, Guid branchId, CancellationToken cancellationToken)
        {
            return await _cache.GetOrSetAsync($"branch:exists:{projectId:N}:{branchId:N}", async ct =>
            {
                const string sql = "SELECT EXISTS (SELECT 1 FROM branches.branches WHERE id = @BranchId AND project_id = @ProjectId);";
                await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
                return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { ProjectId = projectId, BranchId = branchId }, cancellationToken: ct));
            }, 1, cancellationToken);
        }

        /// <summary>
        /// Records a revision on a branch and moves the branch head, in one transaction. Every change must be based
        /// on the file's current head content; otherwise nothing is written and the outdated paths are returned.
        /// </summary>
        /// <remarks>
        /// Taking the next revision number locks the project row first, so concurrent submits to a project run one
        /// after another and the head can't change between the check and the write.
        /// </remarks>
        public async Task<SubmitOutcome> SubmitAsync(Guid projectId, Guid branchId, Guid userId, Guid workspaceId, string host, string message,
            IReadOnlyList<SubmitChange> changes, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            const string numberSql = "UPDATE projects.projects SET revision_counter = revision_counter + 1 WHERE id = @ProjectId RETURNING revision_counter;";
            long number = await connection.ExecuteScalarAsync<long>(new CommandDefinition(numberSql, new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));

            string[] keys = changes.Select(c => c.PathKey).ToArray();
            const string headSql = "SELECT path_key, file_id FROM revisions.head WHERE branch_id = @BranchId AND path_key = ANY(@Keys);";
            Dictionary<string, string> head = (await connection.QueryAsync<(string PathKey, string FileId)>(
                    new CommandDefinition(headSql, new { BranchId = branchId, Keys = keys }, transaction, cancellationToken: cancellationToken)))
                .ToDictionary(row => row.PathKey, row => row.FileId, StringComparer.Ordinal);

            List<string> outOfDate = new();
            List<(SubmitChange Change, short Action)> applied = new();
            foreach (SubmitChange change in changes)
            {
                head.TryGetValue(change.PathKey, out string? current);
                if (!string.Equals(current, change.BaseFileId, StringComparison.Ordinal))
                {
                    outOfDate.Add(change.Path);
                    continue;
                }

                // Deleting a file that isn't on the branch, or "changing" it to what it already is, changes nothing.
                if (string.Equals(current, change.FileId, StringComparison.Ordinal)) continue;

                short action = current is null ? (short)0 : change.FileId is null ? (short)2 : (short)1;
                applied.Add((change, action));
            }

            if (outOfDate.Count > 0) return new SubmitOutcome(null, outOfDate, []);

            // Paths locked by anyone else (or by the submitter in another workspace) can't be submitted. The project
            // row is already locked, so no lock can change before this transaction ends.
            IReadOnlyList<LockRow> locks = await LocksRepository.ReadLocksAsync(connection, transaction, branchId, keys, cancellationToken);
            LockRow[] others = locks.Where(l => l.WorkspaceId != workspaceId).ToArray();
            if (others.Length > 0) return new SubmitOutcome(null, [], others);
            if (applied.Count == 0) return new SubmitOutcome(null, [], []);

            const string revisionSql = "INSERT INTO revisions.revisions (project_id, branch_id, number, user_id, message, created_at) " +
                                       "VALUES (@ProjectId, @BranchId, @Number, @UserId, @Message, @CreatedAt) RETURNING id;";

            long revisionId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(revisionSql,
                new { ProjectId = projectId, BranchId = branchId, Number = number, UserId = userId, Message = message, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                transaction, cancellationToken: cancellationToken));

            // Arrays and unnest write every row in one statement each, however many files the revision has.
            const string changesSql = "INSERT INTO revisions.changes (revision_id, path, action, file_id) " +
                                      "SELECT @RevisionId, t.path, t.action, t.file_id FROM unnest(@Paths, @Actions, @FileIds) AS t(path, action, file_id);";

            await connection.ExecuteAsync(new CommandDefinition(changesSql, new
            {
                RevisionId = revisionId,
                Paths = applied.Select(a => a.Change.Path).ToArray(),
                Actions = applied.Select(a => a.Action).ToArray(),
                FileIds = applied.Select(a => a.Change.FileId).ToArray()
            }, transaction, cancellationToken: cancellationToken));

            SubmitChange[] written = applied.Where(a => a.Change.FileId is not null).Select(a => a.Change).ToArray();
            const string upsertSql = "INSERT INTO revisions.head (branch_id, path_key, path, file_id, revision) " +
                                     "SELECT @BranchId, t.path_key, t.path, t.file_id, @Number FROM unnest(@Keys, @Paths, @FileIds) AS t(path_key, path, file_id) " +
                                     "ON CONFLICT (branch_id, path_key) DO UPDATE SET path = excluded.path, file_id = excluded.file_id, revision = excluded.revision;";

            await connection.ExecuteAsync(new CommandDefinition(upsertSql, new
            {
                BranchId = branchId,
                Number = number,
                Keys = written.Select(c => c.PathKey).ToArray(),
                Paths = written.Select(c => c.Path).ToArray(),
                FileIds = written.Select(c => c.FileId!).ToArray()
            }, transaction, cancellationToken: cancellationToken));

            string[] deleted = applied.Where(a => a.Change.FileId is null).Select(a => a.Change.PathKey).ToArray();
            const string deleteSql = "DELETE FROM revisions.head WHERE branch_id = @BranchId AND path_key = ANY(@Keys);";
            await connection.ExecuteAsync(new CommandDefinition(deleteSql, new { BranchId = branchId, Keys = deleted }, transaction, cancellationToken: cancellationToken));

            // The submitted changes no longer need their locks; then tell everyone about the revision. Both events
            // commit with it (the project row is already locked by the revision counter).
            await LocksRepository.ReleaseSubmittedAsync(connection, transaction, projectId, branchId, userId, workspaceId, host, locks, cancellationToken);
            long eventSeq = await EventStore.AppendAsync(connection, transaction, projectId,
                new NewEvent(EventTypes.RevisionCreated, branchId, userId, workspaceId, host, new RevisionPayload(number, message, applied.Count)), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new SubmitOutcome(number, [], [], eventSeq);
        }

        /// <summary>
        /// The number of the latest revision on a branch, or 0 when nothing was submitted to it yet.
        /// </summary>
        public async Task<long> GetHeadRevisionAsync(Guid branchId, CancellationToken cancellationToken)
        {
            const string sql = "SELECT COALESCE(MAX(number), 0) FROM revisions.revisions WHERE branch_id = @BranchId;";
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, new { BranchId = branchId }, cancellationToken: cancellationToken));
        }

        /// <summary>
        /// One page, ordered by path key, of the files that changed on a branch after <paramref name="afterRevision"/>
        /// up to <paramref name="headRevision"/>: their current content, or a null file id when they were deleted.
        /// </summary>
        public async Task<IReadOnlyList<HeadFileRow>> GetHeadChangesAsync(Guid branchId, long afterRevision, long headRevision, string cursor, int limit, CancellationToken cancellationToken)
        {
            // Files deleted in the range and not re-created since appear once, with the revision that deleted them.
            // A full sync (afterRevision 0) has nothing to delete locally, so skip that half of the query.
            const string sql = "SELECT path_key, path, file_id, revision FROM (" +
                               "  SELECT h.path_key, h.path, h.file_id, h.revision FROM revisions.head h " +
                               "  WHERE h.branch_id = @BranchId AND h.revision > @After AND h.revision <= @Head " +
                               "  UNION ALL " +
                               "  (SELECT DISTINCT ON (lower(c.path)) lower(c.path) AS path_key, c.path, NULL::TEXT AS file_id, r.number AS revision " +
                               "  FROM revisions.revisions r JOIN revisions.changes c ON c.revision_id = r.id " +
                               "  WHERE @After > 0 AND r.branch_id = @BranchId AND r.number > @After AND r.number <= @Head AND c.action = 2 " +
                               "  AND NOT EXISTS (SELECT 1 FROM revisions.head h WHERE h.branch_id = @BranchId AND h.path_key = lower(c.path)) " +
                               "  ORDER BY lower(c.path), r.number DESC)" +
                               ") files WHERE path_key > @Cursor ORDER BY path_key LIMIT @Limit;";

            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return (await connection.QueryAsync<HeadFileRow>(new CommandDefinition(sql,
                new { BranchId = branchId, After = afterRevision, Head = headRevision, Cursor = cursor, Limit = limit },
                cancellationToken: cancellationToken))).ToReadOnly();
        }
    }
}
