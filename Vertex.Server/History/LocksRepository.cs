// Copyright 2026 Entex Interactive

using System.Data;
using Dapper;
using Npgsql;
using Vertex.Core.Net;
using Vertex.Server.Database;

namespace Vertex.Server.History
{
    /// <summary>
    /// A lock on a path, with its holder's username.
    /// </summary>
    public sealed record LockRow(string PathKey, string Path, Guid UserId, Guid WorkspaceId, string Host, string Username, long CreatedAt);

    /// <summary>
    /// A lock event: a path was locked (<see cref="Released"/> false) or released.
    /// </summary>
    public sealed record LockEventRow(long Seq, bool Released, string Path, Guid WorkspaceId, string Host, string Username, long CreatedAt);

    /// <summary>
    /// The outcome of a lock request. <see cref="Granted"/> holds the keys locked now; <see cref="Existing"/> the
    /// current lock on every other requested key. <see cref="Seq"/> is the project's lock seq after the change, or
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
            long seq = await LockProjectAsync(connection, transaction, projectId, ct);

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

            string username = await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT username FROM auth.users WHERE id = @UserId;", new { UserId = userId }, transaction, cancellationToken: ct)) ?? "";
            LockRow[] events = paths.Where(p => granted.Contains(p.Key)).Select(p => new LockRow(p.Key, p.Path, userId, workspaceId, host, username, now)).ToArray();
            long? latest = await AppendEventsAsync(connection, transaction, projectId, branchId, seq, events, released: false, ct);

            await transaction.CommitAsync(ct);
            return new LockOutcome(granted, existing, latest);
        }

        /// <summary>
        /// Releases locks on the given keys held by <paramref name="workspaceId"/>; with <paramref name="force"/>,
        /// also the user's locks from other workspaces, or anyone's when <paramref name="isAdmin"/>.
        /// </summary>
        /// <returns>The released locks and the project's lock seq after the change (null when nothing was released).</returns>
        public async Task<(IReadOnlyList<LockRow> Released, long? Seq)> UnlockAsync(Guid projectId, Guid branchId, Guid userId, Guid workspaceId,
            IReadOnlyList<string> keys, bool force, bool isAdmin, CancellationToken ct)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(ct);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
            long seq = await LockProjectAsync(connection, transaction, projectId, ct);

            const string deleteSql = "WITH deleted AS (DELETE FROM locks.locks l WHERE l.branch_id = @BranchId AND l.path_key = ANY(@Keys) " +
                                     "AND (l.workspace_id = @WorkspaceId OR (@Force AND (l.user_id = @UserId OR @IsAdmin))) RETURNING l.*) " +
                                     "SELECT " + LockColumns + " FROM deleted l JOIN auth.users u ON u.id = l.user_id;";

            LockRow[] freed = (await connection.QueryAsync<LockRow>(new CommandDefinition(deleteSql,
                new { BranchId = branchId, Keys = keys.ToArray(), WorkspaceId = workspaceId, Force = force, UserId = userId, IsAdmin = isAdmin },
                transaction, cancellationToken: ct))).ToArray();

            long? latest = await AppendEventsAsync(connection, transaction, projectId, branchId, seq, freed, released: true, ct);
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
            long latest = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT lock_seq FROM projects.projects WHERE id = @ProjectId;",
                new { ProjectId = projectId }, transaction, cancellationToken: ct));

            if (afterSeq == latest) return new LockChanges(latest, false, [], []);

            // Seqs have no gaps, so a missing next event means it was pruned (or the cursor is from another database).
            bool reset = afterSeq <= 0 || afterSeq > latest || latest - afterSeq > FileTransferLimits.MaximumLockEvents
                         || !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                             "SELECT EXISTS (SELECT 1 FROM locks.events WHERE project_id = @ProjectId AND seq = @Next);",
                             new { ProjectId = projectId, Next = afterSeq + 1 }, transaction, cancellationToken: ct));

            if (reset)
            {
                const string listSql = "SELECT " + LockColumns + " FROM locks.locks l JOIN auth.users u ON u.id = l.user_id WHERE l.branch_id = @BranchId ORDER BY l.path_key;";
                LockRow[] locks = (await connection.QueryAsync<LockRow>(new CommandDefinition(listSql, new { BranchId = branchId }, transaction, cancellationToken: ct))).ToArray();
                return new LockChanges(latest, true, [], locks);
            }

            const string eventsSql = "SELECT e.seq, e.kind = 1 AS released, e.path, e.workspace_id, e.host, u.username, e.created_at " +
                                     "FROM locks.events e JOIN auth.users u ON u.id = e.user_id " +
                                     "WHERE e.project_id = @ProjectId AND e.seq > @After AND e.seq <= @Latest AND e.branch_id = @BranchId ORDER BY e.seq;";

            LockEventRow[] events = (await connection.QueryAsync<LockEventRow>(new CommandDefinition(eventsSql,
                new { ProjectId = projectId, After = afterSeq, Latest = latest, BranchId = branchId }, transaction, cancellationToken: ct))).ToArray();

            return new LockChanges(latest, false, events, []);
        }

        /// <summary>
        /// Deletes lock events created before <paramref name="before"/> (Unix milliseconds).
        /// </summary>
        public async Task<int> PruneEventsAsync(long before, CancellationToken ct)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.ExecuteAsync(new CommandDefinition("DELETE FROM locks.events WHERE created_at < @Before;", new { Before = before }, cancellationToken: ct));
        }

        /// <summary>
        /// Locks the project row and returns its current lock seq. Every transaction that changes locks takes this
        /// first, so they run one at a time per project, commit in seq order and never deadlock with each other.
        /// </summary>
        internal static async Task<long> LockProjectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, CancellationToken ct)
        {
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT lock_seq FROM projects.projects WHERE id = @ProjectId FOR UPDATE;",
                new { ProjectId = projectId }, transaction, cancellationToken: ct));
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
        /// Releases <paramref name="locks"/> (all held by the given workspace) and records their events.
        /// </summary>
        internal static async Task<long?> ReleaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, Guid branchId, long seq,
            IReadOnlyList<LockRow> locks, CancellationToken ct)
        {
            if (locks.Count == 0) return null;
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM locks.locks WHERE branch_id = @BranchId AND path_key = ANY(@Keys);",
                new { BranchId = branchId, Keys = locks.Select(l => l.PathKey).ToArray() }, transaction, cancellationToken: ct));

            return await AppendEventsAsync(connection, transaction, projectId, branchId, seq, locks, released: true, ct);
        }

        /// <summary>
        /// Records one event per lock with consecutive seqs after <paramref name="seq"/> and moves the project's
        /// lock seq. The project row must already be locked (<see cref="LockProjectAsync"/>).
        /// </summary>
        /// <returns>The new lock seq, or null when there was nothing to record.</returns>
        private static async Task<long?> AppendEventsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, Guid branchId, long seq,
            IReadOnlyList<LockRow> locks, bool released, CancellationToken ct)
        {
            if (locks.Count == 0) return null;

            const string eventsSql = "INSERT INTO locks.events (project_id, seq, branch_id, path, kind, user_id, workspace_id, host, created_at) " +
                                     "SELECT @ProjectId, @Seq + t.ord, @BranchId, t.path, @Kind, t.user_id, t.workspace_id, t.host, @Now " +
                                     "FROM unnest(@Paths, @Users, @Workspaces, @Hosts) WITH ORDINALITY AS t(path, user_id, workspace_id, host, ord);";

            await connection.ExecuteAsync(new CommandDefinition(eventsSql, new
            {
                ProjectId = projectId, Seq = seq, BranchId = branchId, Kind = released ? (short)1 : (short)0, Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Paths = locks.Select(l => l.Path).ToArray(),
                Users = locks.Select(l => l.UserId).ToArray(),
                Workspaces = locks.Select(l => l.WorkspaceId).ToArray(),
                Hosts = locks.Select(l => l.Host).ToArray()
            }, transaction, cancellationToken: ct));

            long latest = seq + locks.Count;
            await connection.ExecuteAsync(new CommandDefinition("UPDATE projects.projects SET lock_seq = @Latest WHERE id = @ProjectId;",
                new { Latest = latest, ProjectId = projectId }, transaction, cancellationToken: ct));

            return latest;
        }
    }
}
