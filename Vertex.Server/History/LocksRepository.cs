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
        /// Locks every free path for one workspace and reports who holds the rest, in one transaction.
        /// </summary>
        /// <param name="paths">Distinct path keys with the path as the client spells it.</param>
        public async Task<LockOutcome> LockAsync(Guid projectId, Guid branchId, Guid userId, Guid workspaceId, string host,
            IReadOnlyList<(string Key, string Path)> paths, CancellationToken ct)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(ct);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
            await EventStore.LockProjectAsync(connection, transaction, projectId, "lock", ct);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const string insertSql = "INSERT INTO locks.locks (branch_id, path_key, path, user_id, workspace_id, host, created_at) " +
                                     "SELECT @BranchId, t.path_key, t.path, @UserId, @WorkspaceId, @Host, @Now FROM unnest(@Keys, @Paths) AS t(path_key, path) " +
                                     "ON CONFLICT (branch_id, path_key) DO NOTHING RETURNING path_key;";

            HashSet<string> granted = (await connection.QueryAsync<string>(new CommandDefinition(insertSql, new
            {
                BranchId = branchId, UserId = userId, WorkspaceId = workspaceId, Host = host, Now = now,
                Keys = paths.Select(p => p.Key).ToArray(), Paths = paths.Select(p => p.Path).ToArray()
            }, transaction, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);

            string[] rest = paths.Select(p => p.Key).Where(k => !granted.Contains(k)).ToArray();
            Dictionary<string, LockRow> existing = (await ReadLocksAsync(connection, transaction, branchId, rest, ct)).ToDictionary(l => l.PathKey, StringComparer.Ordinal);

            // One event for the whole operation, however many files it locked.
            string[] lockedPaths = paths.Where(p => granted.Contains(p.Key)).Select(p => p.Path).ToArray();
            long? latest = lockedPaths.Length == 0
                ? null
                : await EventStore.AppendAsync(connection, transaction, projectId,
                    new NewEvent(EventTypes.LockLocked, branchId, userId, workspaceId, host, new LockPayload(lockedPaths)), ct);

            await transaction.CommitAsync(ct);
            return new LockOutcome(granted, existing, latest);
        }

        /// <summary>
        /// Releases locks on the given keys held by <paramref name="workspaceId"/>; with <paramref name="force"/>,
        /// also the user's locks from other workspaces, or anyone's when <paramref name="isAdmin"/>.
        /// </summary>
        /// <returns>The released locks and the project's event seq after the change (null when nothing was released).</returns>
        public async Task<(IReadOnlyList<LockRow> Released, long? Seq)> UnlockAsync(Guid projectId, Guid branchId, Guid userId, Guid workspaceId,
            IReadOnlyList<string> keys, bool force, bool isAdmin, CancellationToken ct)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(ct);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
            await EventStore.LockProjectAsync(connection, transaction, projectId, "unlock", ct);

            const string deleteSql = "WITH deleted AS (DELETE FROM locks.locks l WHERE l.branch_id = @BranchId AND l.path_key = ANY(@Keys) " +
                                     "AND (l.workspace_id = @WorkspaceId OR (@Force AND (l.user_id = @UserId OR @IsAdmin))) RETURNING l.*) " +
                                     "SELECT " + LockColumns + " FROM deleted l JOIN auth.users u ON u.id = l.user_id;";

            LockRow[] freed = (await connection.QueryAsync<LockRow>(new CommandDefinition(deleteSql,
                new { BranchId = branchId, Keys = keys.ToArray(), WorkspaceId = workspaceId, Force = force, UserId = userId, IsAdmin = isAdmin },
                transaction, cancellationToken: ct))).ToArray();

            // Without force the workspace releases its own locks on files it no longer changes; with force someone
            // cleared locks held elsewhere.
            long? latest = freed.Length == 0
                ? null
                : await EventStore.AppendAsync(connection, transaction, projectId, new NewEvent(EventTypes.LockReleased, branchId, userId, workspaceId,
                    force ? null : freed[0].Host, new LockPayload(freed.Select(l => l.Path).ToArray(), force ? LockReleaseReasons.Unlocked : LockReleaseReasons.Reverted)), ct);

            await transaction.CommitAsync(ct);
            return (freed, latest);
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
            long latest = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT event_seq FROM projects.projects WHERE id = @ProjectId;",
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
        /// Current locks on some keys of a branch.
        /// </summary>
        internal static async Task<IReadOnlyList<LockRow>> ReadLocksAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid branchId, string[] keys, CancellationToken ct)
        {
            if (keys.Length == 0) return [];
            const string sql = "SELECT " + LockColumns + " FROM locks.locks l JOIN auth.users u ON u.id = l.user_id WHERE l.branch_id = @BranchId AND l.path_key = ANY(@Keys);";
            return (await connection.QueryAsync<LockRow>(new CommandDefinition(sql, new { BranchId = branchId, Keys = keys }, transaction, cancellationToken: ct))).ToArray();
        }

        /// <summary>
        /// Releases a submitter's <paramref name="locks"/> in the submit's transaction and records one event for them.
        /// The project row must already be locked.
        /// </summary>
        /// <returns>The event's seq, or null when there was nothing to release.</returns>
        internal static async Task<long?> ReleaseSubmittedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, Guid branchId,
            Guid userId, Guid workspaceId, string host, IReadOnlyList<LockRow> locks, CancellationToken ct)
        {
            if (locks.Count == 0) return null;
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM locks.locks WHERE branch_id = @BranchId AND path_key = ANY(@Keys);",
                new { BranchId = branchId, Keys = locks.Select(l => l.PathKey).ToArray() }, transaction, cancellationToken: ct));

            // A submit can hold far more files than one stream message may carry, so its release is split into events of
            // at most one lock request's worth of paths.
            long? seq = null;
            foreach (string[] paths in locks.Select(l => l.Path).Chunk(FileTransferLimits.MaximumPathsPerLockRequest))
            {
                seq = await EventStore.AppendAsync(connection, transaction, projectId, new NewEvent(EventTypes.LockReleased, branchId, userId, workspaceId, host,
                    new LockPayload(paths, LockReleaseReasons.Submitted)), ct);
            }

            return seq;
        }
    }
}
