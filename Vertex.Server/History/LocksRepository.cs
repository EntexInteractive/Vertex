// Copyright 2026 Entex Interactive

using System.Data;
using Dapper;
using Npgsql;
using Vertex.Core.Net;
using Vertex.Server.Database;
using Vertex.Server.Events;

namespace Vertex.Server.History
{
    /// <summary>
    /// A lock on a path, with its holder's username.
    /// </summary>
    public sealed record LockRow(string PathKey, string Path, Guid UserId, Guid WorkspaceId, string Host, string Username, long CreatedAt);

    /// <summary>
    /// A lock event: a path was locked (<see cref="Released"/> false) or released.
    /// </summary>
    public sealed record LockEventRow(long Seq, bool Released, string Path, Guid? WorkspaceId, string? Host, string Username, long CreatedAt);

    /// <summary>
    /// The outcome of a lock request. <see cref="Granted"/> holds the keys locked now; <see cref="Existing"/> the
    /// current lock on every other requested key. <see cref="Seq"/> is the project's event seq after the change, or
    /// null when nothing changed.
    /// </summary>
    public sealed record LockOutcome(IReadOnlySet<string> Granted, IReadOnlyDictionary<string, LockRow> Existing, long? Seq);

    /// <summary>
    /// The outcome of an unlock request: the locks released and the project's event seq after the change, or null when
    /// nothing was released.
    /// </summary>
    public sealed record UnlockOutcome(IReadOnlyList<LockRow> Released, long? Seq);

    /// <summary>
    /// One validated lock request. <see cref="Paths"/> are distinct path keys with the path as the client spells it.
    /// </summary>
    public sealed record LockWrite(Guid ProjectId, Guid BranchId, Guid UserId, Guid WorkspaceId, string Host, IReadOnlyList<(string Key, string Path)> Paths);

    /// <summary>
    /// One validated unlock request.
    /// </summary>
    public sealed record UnlockWrite(Guid ProjectId, Guid BranchId, Guid UserId, Guid WorkspaceId, IReadOnlyList<string> Keys, bool Force, bool IsAdmin);

    /// <summary>
    /// What changed since a client's cursor: events after it, or (when <see cref="Reset"/>) the branch's full list.
    /// </summary>
    public sealed record LockChanges(long LatestSeq, bool Reset, IReadOnlyList<LockEventRow> Events, IReadOnlyList<LockRow> Locks);

    public class LocksRepository
    {
        private const string LockColumns = "l.path_key, l.path, l.user_id, l.workspace_id, l.host, u.username, l.created_at";

        private readonly PostgresDatabase _database;

        public LocksRepository(PostgresDatabase database)
        {
            _database = database;
        }

        /// <summary>
        /// Locks every free path of one request and returns, in one result set: the keys granted (kind 0), the current
        /// lock on every other requested key (kind 1), and the event seq when anything was locked (kind 2, in
        /// created_at). Runs inside a transaction that already holds the project's counters row
        /// (<see cref="EventStore.LockProjectSql"/>), so the locks it reads are exactly the committed ones, and several
        /// requests can share that transaction (<see cref="ProjectWriteQueue"/>).
        /// </summary>
        private const string LockSql =
            "WITH req AS (SELECT t.path_key, t.path, t.ord FROM unnest(@Keys::TEXT[], @Paths::TEXT[]) WITH ORDINALITY AS t(path_key, path, ord)), " +
            "ins AS (INSERT INTO locks.locks (branch_id, path_key, path, user_id, workspace_id, host, created_at) " +
            "  SELECT @BranchId, r.path_key, r.path, @UserId, @WorkspaceId, @Host, @Now FROM req r ORDER BY r.ord " +
            "  ON CONFLICT (branch_id, path_key) DO NOTHING RETURNING path_key), " +
            "next AS (UPDATE projects.counters SET event_seq = event_seq + 1 WHERE project_id = @ProjectId AND EXISTS (SELECT 1 FROM ins) RETURNING event_seq), " +
            "ev AS (INSERT INTO events.events (project_id, seq, type, branch_id, user_id, workspace_id, host, payload, created_at) " +
            "  SELECT @ProjectId, next.event_seq, @Type, @BranchId, @UserId, @WorkspaceId, @Host, " +
            "  jsonb_build_object('paths', (SELECT jsonb_agg(r.path ORDER BY r.ord) FROM req r JOIN ins i ON i.path_key = r.path_key), 'reason', NULL), @Now " +
            "  FROM next RETURNING seq) " +
            "SELECT 0 AS kind, i.path_key, NULL::TEXT AS path, NULL::UUID AS user_id, NULL::UUID AS workspace_id, NULL::TEXT AS host, NULL::TEXT AS username, NULL::BIGINT AS created_at FROM ins i " +
            "UNION ALL SELECT 1, l.path_key, l.path, l.user_id, l.workspace_id, l.host, u.username, l.created_at FROM locks.locks l JOIN auth.users u ON u.id = l.user_id " +
            "  WHERE l.branch_id = @BranchId AND l.path_key IN (SELECT path_key FROM req) AND l.path_key NOT IN (SELECT path_key FROM ins) " +
            "UNION ALL SELECT 2, NULL, NULL, NULL, NULL, NULL, NULL, ev.seq FROM ev;";

        /// <summary>
        /// Releases one request's locks and returns, in one result set: the released locks (kind 0) and the event seq
        /// when anything was released (kind 1, in created_at). Same transaction rules as <see cref="LockSql"/>.
        /// Without force a workspace releases only its own locks; with force also the caller's locks from other
        /// workspaces, or anyone's for a project admin.
        /// </summary>
        private const string UnlockSql =
            "WITH del AS (DELETE FROM locks.locks l WHERE l.branch_id = @BranchId AND l.path_key = ANY(@Keys::TEXT[]) " +
            "  AND (l.workspace_id = @WorkspaceId OR (@Force AND (l.user_id = @UserId OR @IsAdmin))) " +
            "  RETURNING l.path_key, l.path, l.user_id, l.workspace_id, l.host, l.created_at), " +
            "next AS (UPDATE projects.counters SET event_seq = event_seq + 1 WHERE project_id = @ProjectId AND EXISTS (SELECT 1 FROM del) RETURNING event_seq), " +
            "ev AS (INSERT INTO events.events (project_id, seq, type, branch_id, user_id, workspace_id, host, payload, created_at) " +
            "  SELECT @ProjectId, next.event_seq, @Type, @BranchId, @UserId, @WorkspaceId, CASE WHEN @Force THEN NULL ELSE (SELECT host FROM del LIMIT 1) END, " +
            "  jsonb_build_object('paths', (SELECT jsonb_agg(path) FROM del), 'reason', @Reason::TEXT), @Now " +
            "  FROM next RETURNING seq) " +
            "SELECT 0 AS kind, d.path_key, d.path, d.user_id, d.workspace_id, d.host, u.username, d.created_at FROM del d JOIN auth.users u ON u.id = d.user_id " +
            "UNION ALL SELECT 1, NULL, NULL, NULL, NULL, NULL, NULL, ev.seq FROM ev;";

        /// <summary>
        /// The statement for one lock request, for a batch that already holds the project's counters row.
        /// </summary>
        internal static NpgsqlBatchCommand CreateLockCommand(LockWrite write, long now)
        {
            NpgsqlBatchCommand command = new(LockSql);
            NpgsqlParameterCollection p = command.Parameters;
            p.Add(new NpgsqlParameter<string[]>("Keys", write.Paths.Select(x => x.Key).ToArray()));
            p.Add(new NpgsqlParameter<string[]>("Paths", write.Paths.Select(x => x.Path).ToArray()));
            p.Add(new NpgsqlParameter<Guid>("ProjectId", write.ProjectId));
            p.Add(new NpgsqlParameter<Guid>("BranchId", write.BranchId));
            p.Add(new NpgsqlParameter<Guid>("UserId", write.UserId));
            p.Add(new NpgsqlParameter<Guid>("WorkspaceId", write.WorkspaceId));
            p.Add(new NpgsqlParameter<string>("Host", write.Host));
            p.Add(new NpgsqlParameter<string>("Type", EventTypes.LockLocked));
            p.Add(new NpgsqlParameter<long>("Now", now));
            return command;
        }

        /// <summary>
        /// Reads the result set of <see cref="CreateLockCommand"/>.
        /// </summary>
        internal static async Task<LockOutcome> ReadLockAsync(NpgsqlDataReader reader, CancellationToken ct)
        {
            HashSet<string> granted = new(StringComparer.Ordinal);
            Dictionary<string, LockRow> existing = new(StringComparer.Ordinal);
            long? seq = null;
            while (await reader.ReadAsync(ct))
            {
                switch (reader.GetInt32(0))
                {
                    case 0:
                        granted.Add(reader.GetString(1));
                        break;
                    case 1:
                        LockRow row = ReadLockRow(reader);
                        existing[row.PathKey] = row;
                        break;
                    default:
                        seq = reader.GetInt64(7);
                        break;
                }
            }

            return new LockOutcome(granted, existing, seq);
        }

        /// <summary>
        /// The statement for one unlock request, for a batch that already holds the project's counters row.
        /// </summary>
        internal static NpgsqlBatchCommand CreateUnlockCommand(UnlockWrite write, long now)
        {
            NpgsqlBatchCommand command = new(UnlockSql);
            NpgsqlParameterCollection p = command.Parameters;
            p.Add(new NpgsqlParameter<string[]>("Keys", write.Keys.ToArray()));
            p.Add(new NpgsqlParameter<Guid>("ProjectId", write.ProjectId));
            p.Add(new NpgsqlParameter<Guid>("BranchId", write.BranchId));
            p.Add(new NpgsqlParameter<Guid>("UserId", write.UserId));
            p.Add(new NpgsqlParameter<Guid>("WorkspaceId", write.WorkspaceId));
            p.Add(new NpgsqlParameter<bool>("Force", write.Force));
            p.Add(new NpgsqlParameter<bool>("IsAdmin", write.IsAdmin));
            p.Add(new NpgsqlParameter<string>("Type", EventTypes.LockReleased));
            p.Add(new NpgsqlParameter<string>("Reason", write.Force ? LockReleaseReasons.Unlocked : LockReleaseReasons.Reverted));
            p.Add(new NpgsqlParameter<long>("Now", now));
            return command;
        }

        /// <summary>
        /// Reads the result set of <see cref="CreateUnlockCommand"/>.
        /// </summary>
        internal static async Task<UnlockOutcome> ReadUnlockAsync(NpgsqlDataReader reader, CancellationToken ct)
        {
            List<LockRow> released = new();
            long? seq = null;
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetInt32(0) == 0) released.Add(ReadLockRow(reader));
                else seq = reader.GetInt64(7);
            }

            return new UnlockOutcome(released, seq);
        }

        private static LockRow ReadLockRow(NpgsqlDataReader reader)
        {
            return new LockRow(reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7));
        }

        /// <summary>
        /// The lock events on a branch after <paramref name="afterSeq"/>, or the branch's full lock list when the
        /// cursor is 0, older than the retained events, or too far behind.
        /// </summary>
        public async Task<LockChanges> GetChangesAsync(Guid projectId, Guid branchId, long afterSeq, CancellationToken ct)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(ct);

            // One snapshot, so the list or the events match latest_seq exactly.
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            long latest = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT event_seq FROM projects.counters WHERE project_id = @ProjectId;",
                new { ProjectId = projectId }, transaction, cancellationToken: ct));

            if (afterSeq == latest) return new LockChanges(latest, false, [], []);

            // Seqs have no gaps, so a missing next event means it was pruned (or the cursor is from another database).
            bool reset = afterSeq <= 0 || afterSeq > latest || latest - afterSeq > FileTransferLimits.MaximumLockEvents
                         || !await EventStore.ExistsAsync(connection, transaction, projectId, afterSeq + 1, ct);

            if (reset)
            {
                const string listSql = "SELECT " + LockColumns + " FROM locks.locks l JOIN auth.users u ON u.id = l.user_id WHERE l.branch_id = @BranchId ORDER BY l.path_key;";
                LockRow[] locks = (await connection.QueryAsync<LockRow>(new CommandDefinition(listSql, new { BranchId = branchId }, transaction, cancellationToken: ct))).ToArray();
                return new LockChanges(latest, true, [], locks);
            }

            // One row per path, as the lock mirror applies them.
            List<LockEventRow> events = new();
            foreach (ProjectEvent lockEvent in await EventStore.ReadAsync(connection, transaction, projectId, afterSeq, latest, "lock.", branchId, ct))
            {
                bool released = lockEvent.Type == EventTypes.LockReleased;
                foreach (string path in lockEvent.Read<LockPayload>().Paths)
                    events.Add(new LockEventRow(lockEvent.Seq, released, path, lockEvent.WorkspaceId, lockEvent.Host, lockEvent.Username, lockEvent.CreatedAt));
            }

            return new LockChanges(latest, false, events, []);
        }

        /// <summary>
        /// Current locks on some keys of a branch (<c>@BranchId</c>, <c>@Keys</c>), as <see cref="LockRow"/> columns.
        /// </summary>
        internal const string ReadLocksSql = "SELECT " + LockColumns + " FROM locks.locks l JOIN auth.users u ON u.id = l.user_id WHERE l.branch_id = @BranchId AND l.path_key = ANY(@Keys);";

        /// <summary>
        /// Adds the commands that release a submitter's <paramref name="locks"/> to the submit's write batch and record
        /// events for them. The batch's transaction must already hold the project's counters row.
        /// </summary>
        internal static void AddReleaseSubmittedCommands(NpgsqlBatch batch, Guid projectId, Guid branchId, Guid userId, Guid workspaceId, string host,
            IReadOnlyList<LockRow> locks)
        {
            if (locks.Count == 0) return;
            NpgsqlBatchCommand delete = new("DELETE FROM locks.locks WHERE branch_id = @BranchId AND path_key = ANY(@Keys);");
            delete.Parameters.Add(new NpgsqlParameter<Guid>("BranchId", branchId));
            delete.Parameters.Add(new NpgsqlParameter<string[]>("Keys", locks.Select(l => l.PathKey).ToArray()));
            batch.BatchCommands.Add(delete);

            // A submit can hold far more files than one stream message may carry, so its release is split into events of
            // at most one lock request's worth of paths.
            foreach (string[] paths in locks.Select(l => l.Path).Chunk(FileTransferLimits.MaximumPathsPerLockRequest))
            {
                batch.BatchCommands.Add(EventStore.CreateAppendCommand(projectId,
                    new NewEvent(EventTypes.LockReleased, branchId, userId, workspaceId, host, new LockPayload(paths, LockReleaseReasons.Submitted))));
            }
        }
    }
}
