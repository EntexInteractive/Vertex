// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Vertex.Server.Database;
using Vertex.Server.Events;
using Vertex.Server.Telemetry;

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
    /// <param name="LocksReleased">How many of the submitter's locks the submit released.</param>
    public sealed record SubmitOutcome(long? Revision, IReadOnlyList<string> OutOfDate, IReadOnlyList<LockRow> LockedByOthers, long? EventSeq = null, int LocksReleased = 0);

    /// <summary>
    /// A file on a branch, for sync. <see cref="FileId"/> is null when the file was deleted.
    /// </summary>
    public sealed record HeadFileRow(string PathKey, string Path, string? FileId, long Revision);

    /// <summary>
    /// One revision in a branch's history, with who made it and how many files it changed.
    /// </summary>
    public sealed record RevisionRow(long Number, string Message, string Username, string? DisplayName, long CreatedAt, int Files);

    /// <summary>
    /// One file a revision changed. <see cref="FileId"/> is null when the revision deleted it.
    /// </summary>
    public sealed record RevisionChangeRow(string Path, string? FileId);

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
            return await _cache.GetOrSetAsync(CacheKeys.BranchExists(projectId, branchId), async ct =>
            {
                const string sql = "SELECT EXISTS (SELECT 1 FROM branches.branches WHERE id = @BranchId AND project_id = @ProjectId);";
                await using NpgsqlConnection connection = _database.CreateConnection();
                return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { ProjectId = projectId, BranchId = branchId }, cancellationToken: ct));
            }, 1, cancellationToken);
        }

        /// <summary>
        /// Records a revision on a branch and moves the branch head, in one transaction. Every change must be based
        /// on the file's current head content; otherwise nothing is written and the outdated paths are returned.
        /// </summary>
        /// <remarks>
        /// Taking the next revision number locks the project's counters row first, so writes to a project run one after
        /// another and the head can't change between the check and the write. Everything else is batched so the row is
        /// held for as few round trips as possible: one to read the head and the locks, one to write, and the commit.
        /// </remarks>
        public async Task<SubmitOutcome> SubmitAsync(Guid projectId, Guid branchId, Guid userId, Guid workspaceId, string host, string message, IReadOnlyList<SubmitChange> changes, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            long number;
            await using (NpgsqlCommand numberCommand = new("UPDATE projects.counters SET revision_counter = revision_counter + 1 WHERE project_id = @ProjectId RETURNING revision_counter;",
                             connection, transaction))
            {
                numberCommand.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                using (VertexMetrics.Time(VertexMetrics.ProjectLockWait, "operation", "submit"))
                    number = (long)(await numberCommand.ExecuteScalarAsync(cancellationToken))!;
            }

            long locked = Stopwatch.GetTimestamp();
            string[] keys = changes.Select(c => c.PathKey).ToArray();
            Dictionary<string, string> head = new(StringComparer.Ordinal);
            List<LockRow> locks = new();
            await using (NpgsqlBatch read = new(connection, transaction))
            {
                NpgsqlBatchCommand headCommand = new("SELECT path_key, file_id FROM revisions.head WHERE branch_id = @BranchId AND path_key = ANY(@Keys);");
                headCommand.Parameters.Add(new NpgsqlParameter<Guid>("BranchId", branchId));
                headCommand.Parameters.Add(new NpgsqlParameter<string[]>("Keys", keys));
                read.BatchCommands.Add(headCommand);

                // Paths locked by anyone else (or by the submitter in another workspace) can't be submitted. The counters
                // row is already locked, so no lock can change before this transaction ends.
                NpgsqlBatchCommand locksCommand = new(LocksRepository.ReadLocksSql);
                locksCommand.Parameters.Add(new NpgsqlParameter<Guid>("BranchId", branchId));
                locksCommand.Parameters.Add(new NpgsqlParameter<string[]>("Keys", keys));
                read.BatchCommands.Add(locksCommand);

                await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) head[reader.GetString(0)] = reader.GetString(1);
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    locks.Add(new LockRow(reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
            }

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
            LockRow[] others = locks.Where(l => l.WorkspaceId != workspaceId).ToArray();
            if (others.Length > 0) return new SubmitOutcome(null, [], others);
            if (applied.Count == 0) return new SubmitOutcome(null, [], []);

            long eventSeq = 0;
            await using (NpgsqlBatch write = new(connection, transaction))
            {
                // The revision and its changes in one statement; arrays and unnest write every row at once, however many
                // files the revision has.
                NpgsqlBatchCommand revision = new(
                    "WITH r AS (INSERT INTO revisions.revisions (project_id, branch_id, number, user_id, message, created_at) " +
                    "VALUES (@ProjectId, @BranchId, @Number, @UserId, @Message, @CreatedAt) RETURNING id) " +
                    "INSERT INTO revisions.changes (revision_id, path, action, file_id) " +
                    "SELECT r.id, t.path, t.action, t.file_id FROM r, unnest(@Paths::TEXT[], @Actions::SMALLINT[], @FileIds::TEXT[]) AS t(path, action, file_id);");
                revision.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                revision.Parameters.Add(new NpgsqlParameter<Guid>("BranchId", branchId));
                revision.Parameters.Add(new NpgsqlParameter<long>("Number", number));
                revision.Parameters.Add(new NpgsqlParameter<Guid>("UserId", userId));
                revision.Parameters.Add(new NpgsqlParameter<string>("Message", message));
                revision.Parameters.Add(new NpgsqlParameter<long>("CreatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                revision.Parameters.Add(new NpgsqlParameter<string[]>("Paths", applied.Select(a => a.Change.Path).ToArray()));
                revision.Parameters.Add(new NpgsqlParameter<short[]>("Actions", applied.Select(a => a.Action).ToArray()));
                revision.Parameters.Add(new NpgsqlParameter("FileIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = applied.Select(a => a.Change.FileId).ToArray() });
                write.BatchCommands.Add(revision);

                SubmitChange[] written = applied.Where(a => a.Change.FileId is not null).Select(a => a.Change).ToArray();
                if (written.Length > 0)
                {
                    NpgsqlBatchCommand upsert = new("INSERT INTO revisions.head (branch_id, path_key, path, file_id, revision) " +
                                                    "SELECT @BranchId, t.path_key, t.path, t.file_id, @Number FROM unnest(@Keys::TEXT[], @Paths::TEXT[], @FileIds::TEXT[]) AS t(path_key, path, file_id) " +
                                                    "ON CONFLICT (branch_id, path_key) DO UPDATE SET path = excluded.path, file_id = excluded.file_id, revision = excluded.revision;");
                    upsert.Parameters.Add(new NpgsqlParameter<Guid>("BranchId", branchId));
                    upsert.Parameters.Add(new NpgsqlParameter<long>("Number", number));
                    upsert.Parameters.Add(new NpgsqlParameter<string[]>("Keys", written.Select(c => c.PathKey).ToArray()));
                    upsert.Parameters.Add(new NpgsqlParameter<string[]>("Paths", written.Select(c => c.Path).ToArray()));
                    upsert.Parameters.Add(new NpgsqlParameter<string[]>("FileIds", written.Select(c => c.FileId!).ToArray()));
                    write.BatchCommands.Add(upsert);
                }

                string[] deleted = applied.Where(a => a.Change.FileId is null).Select(a => a.Change.PathKey).ToArray();
                if (deleted.Length > 0)
                {
                    NpgsqlBatchCommand delete = new("DELETE FROM revisions.head WHERE branch_id = @BranchId AND path_key = ANY(@Keys);");
                    delete.Parameters.Add(new NpgsqlParameter<Guid>("BranchId", branchId));
                    delete.Parameters.Add(new NpgsqlParameter<string[]>("Keys", deleted));
                    write.BatchCommands.Add(delete);
                }

                // The submitted changes no longer need their locks; then tell everyone about the revision. Both events
                // commit with it, and the revision's is the last row the batch returns.
                LocksRepository.AddReleaseSubmittedCommands(write, projectId, branchId, userId, workspaceId, host, locks);
                write.BatchCommands.Add(EventStore.CreateAppendCommand(projectId,
                    new NewEvent(EventTypes.RevisionCreated, branchId, userId, workspaceId, host, new RevisionPayload(number, message, applied.Count))));

                await using NpgsqlDataReader reader = await write.ExecuteReaderAsync(cancellationToken);
                do
                {
                    while (await reader.ReadAsync(cancellationToken)) eventSeq = reader.GetInt64(0);
                } while (await reader.NextResultAsync(cancellationToken));
            }

            long commit = Stopwatch.GetTimestamp();
            await transaction.CommitAsync(cancellationToken);
            long done = Stopwatch.GetTimestamp();
            VertexMetrics.CommitDuration.Record(Stopwatch.GetElapsedTime(commit, done).TotalSeconds, VertexMetrics.Tag("operation", "submit"));
            VertexMetrics.ProjectLockHeld.Record(Stopwatch.GetElapsedTime(locked, done).TotalSeconds, VertexMetrics.Tag("operation", "submit"));
            return new SubmitOutcome(number, [], [], eventSeq, locks.Count);
        }

        /// <summary>
        /// How long a cached head revision lives. Short, so a lost update can only hide a new revision briefly.
        /// </summary>
        private static readonly TimeSpan HeadRevisionLifetime = TimeSpan.FromSeconds(60);

        /// <summary>
        /// The number of the latest revision on a branch (or the revision it was copied from its parent at, when
        /// nothing was submitted to it since), or 0 when it has no files yet. Every sync asks,
        /// so it's answered from Redis when it can be.
        /// </summary>
        /// <remarks>
        /// Kept with <see cref="CacheContext.SetMaxAsync"/> rather than as a cached value: a reader that queried just
        /// before a submit can't write the older number back over the newer one.
        /// </remarks>
        public async Task<long> GetHeadRevisionAsync(Guid branchId, CancellationToken cancellationToken)
        {
            string key = CacheKeys.HeadRevision(branchId);
            if (await _cache.GetInt64Async(key) is long cached)
            {
                VertexMetrics.CacheLookups.Add(1, VertexMetrics.Tag("result", "hit"), VertexMetrics.Tag("category", "revision"));
                return cached;
            }

            VertexMetrics.CacheLookups.Add(1, VertexMetrics.Tag("result", "miss"), VertexMetrics.Tag("category", "revision"));
            // A branch copied from a parent is at least at the revision it was copied at.
            const string sql = "SELECT GREATEST(COALESCE((SELECT base_revision FROM branches.branches WHERE id = @BranchId), 0), COALESCE((SELECT MAX(number) FROM revisions.revisions WHERE branch_id = @BranchId), 0));";
            await using NpgsqlConnection connection = _database.CreateConnection();
            long head = await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, new { BranchId = branchId }, cancellationToken: cancellationToken));
            await _cache.SetMaxAsync(key, head, HeadRevisionLifetime);
            return head;
        }

        /// <summary>
        /// Records a branch's new head revision after a submit commits, so the next sync sees it without asking
        /// PostgreSQL.
        /// </summary>
        public Task CacheHeadRevisionAsync(Guid branchId, long revision)
        {
            return _cache.SetMaxAsync(CacheKeys.HeadRevision(branchId), revision, HeadRevisionLifetime);
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

            await using NpgsqlConnection connection = _database.CreateConnection();
            return (await connection.QueryAsync<HeadFileRow>(new CommandDefinition(sql, new { BranchId = branchId, After = afterRevision, Head = headRevision, Cursor = cursor, Limit = limit }, cancellationToken: cancellationToken))).ToReadOnly();
        }

        /// <summary>
        /// The parent revision a branch started from (<c>branches.base_revision</c>), or 0 when it started empty.
        /// Every incremental sync asks, and it never changes, so it's cached.
        /// </summary>
        public async Task<long> GetBaseRevisionAsync(Guid branchId, CancellationToken cancellationToken)
        {
            return await _cache.GetOrSetAsync(CacheKeys.BranchBase(branchId), async ct =>
            {
                const string sql = "SELECT COALESCE((SELECT base_revision FROM branches.branches WHERE id = @BranchId), 0);";
                await using NpgsqlConnection connection = _database.CreateConnection();
                return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, new { BranchId = branchId }, cancellationToken: ct));
            }, 60, cancellationToken);
        }

        /// <summary>
        /// One page, ordered by path key, of every file on a branch as of <paramref name="revision"/>, rebuilt from
        /// history: the branch's own revisions up to it, then its parent's up to the branch's base revision, and so on
        /// up the branches it was copied from.
        /// </summary>
        /// <remarks>
        /// Costs a pass over the branch's history up to the revision, so only syncs to an older revision use it;
        /// syncing to the latest reads <c>revisions.head</c>. Revision numbers are project-wide, so the newest change
        /// to a path across the lineage is the one in effect.
        /// </remarks>
        public async Task<IReadOnlyList<HeadFileRow>> GetFilesAtRevisionAsync(Guid branchId, long revision, string cursor, int limit, CancellationToken cancellationToken)
        {
            const string sql = "WITH RECURSIVE lineage AS (" +
                               "  SELECT id, parent_id, base_revision, @Revision::BIGINT AS upto FROM branches.branches WHERE id = @BranchId " +
                               "  UNION ALL " +
                               "  SELECT p.id, p.parent_id, p.base_revision, LEAST(l.upto, l.base_revision) " +
                               "  FROM lineage l JOIN branches.branches p ON p.id = l.parent_id WHERE l.base_revision > 0" +
                               ") " +
                               "SELECT path_key, path, file_id, revision FROM (" +
                               "  SELECT DISTINCT ON (lower(c.path)) lower(c.path) AS path_key, c.path, c.file_id, r.number AS revision " +
                               "  FROM lineage l JOIN revisions.revisions r ON r.branch_id = l.id AND r.number <= l.upto " +
                               "  JOIN revisions.changes c ON c.revision_id = r.id " +
                               "  WHERE lower(c.path) > @Cursor " +
                               "  ORDER BY lower(c.path), r.number DESC" +
                               ") files WHERE file_id IS NOT NULL ORDER BY path_key LIMIT @Limit;";

            await using NpgsqlConnection connection = _database.CreateConnection();
            return (await connection.QueryAsync<HeadFileRow>(new CommandDefinition(sql, new { BranchId = branchId, Revision = revision, Cursor = cursor, Limit = limit },
                commandTimeout: 300, cancellationToken: cancellationToken))).ToReadOnly();
        }

        /// <summary>
        /// Up to <paramref name="limit"/> revisions on a branch after <paramref name="afterRevision"/>, oldest first.
        /// </summary>
        public async Task<IReadOnlyList<RevisionRow>> GetRevisionsAsync(Guid branchId, long afterRevision, int limit, CancellationToken cancellationToken)
        {
            const string sql = "SELECT r.number, r.message, u.username, u.display_name AS DisplayName, r.created_at AS CreatedAt, (SELECT COUNT(*)::INT FROM revisions.changes c WHERE c.revision_id = r.id) AS Files FROM revisions.revisions r JOIN auth.users u ON u.id = r.user_id WHERE r.branch_id = @BranchId AND r.number > @After ORDER BY r.number LIMIT @Limit;";
            await using NpgsqlConnection connection = _database.CreateConnection();
            return (await connection.QueryAsync<RevisionRow>(new CommandDefinition(sql, new { BranchId = branchId, After = afterRevision, Limit = limit }, cancellationToken: cancellationToken))).ToReadOnly();
        }

        /// <summary>
        /// One page, ordered by path, of the files a revision on a branch changed after <paramref name="cursor"/>.
        /// Empty when the revision isn't on the branch (every revision changes at least one file).
        /// </summary>
        public async Task<IReadOnlyList<RevisionChangeRow>> GetRevisionChangesAsync(Guid projectId, Guid branchId, long revision, string cursor, int limit, CancellationToken cancellationToken)
        {
            const string sql = "SELECT c.path, c.file_id AS FileId FROM revisions.revisions r JOIN revisions.changes c ON c.revision_id = r.id WHERE r.project_id = @ProjectId AND r.number = @Number AND r.branch_id = @BranchId AND c.path > @Cursor ORDER BY c.path LIMIT @Limit;";
            await using NpgsqlConnection connection = _database.CreateConnection();
            return (await connection.QueryAsync<RevisionChangeRow>(new CommandDefinition(sql,
                new { ProjectId = projectId, BranchId = branchId, Number = revision, Cursor = cursor, Limit = limit }, cancellationToken: cancellationToken))).ToReadOnly();
        }
    }
}
