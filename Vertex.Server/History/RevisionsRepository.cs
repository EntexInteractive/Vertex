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
        /// Taking the next revision number locks the project's counters row first, so writes to a project run one after
        /// another and the head can't change between the check and the write. Everything else is batched so the row is
        /// held for as few round trips as possible: one to read the head and the locks, one to write, and the commit.
        /// </remarks>
        public async Task<SubmitOutcome> SubmitAsync(Guid projectId, Guid branchId, Guid userId, Guid workspaceId, string host, string message,
            IReadOnlyList<SubmitChange> changes, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
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
