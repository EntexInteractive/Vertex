// Copyright 2026 Entex Interactive

using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Vertex.Server.Database;
using Vertex.Server.Telemetry;

namespace Vertex.Server.Events
{
    /// <summary>
    /// The project event journal (<c>events.events</c>).
    /// </summary>
    /// <remarks>
    /// Changes record their events with <see cref="AppendAsync"/> inside their own transaction (so an event exists
    /// exactly when its change committed), then call <see cref="INotificationService.NotifyAsync"/> after commit.
    /// Appending bumps the project's <c>projects.counters.event_seq</c>, which locks that row until commit: seqs are
    /// gapless and commit in order, so a reader resuming after seq N never skips an event.
    /// </remarks>
    public class EventStore
    {
        private const string Columns = "e.project_id, e.seq, e.type, e.branch_id, e.user_id, u.username, e.workspace_id, e.host, e.payload::TEXT AS payload, e.created_at";

        private readonly PostgresDatabase _database;

        public EventStore(ServerSettings settings)
        {
            _database = new PostgresDatabase(settings);
        }

        /// <summary>
        /// Locks the project's counters row and returns its current event seq. Every transaction that writes to a
        /// project takes this first, so writes serialize per project and never deadlock with each other.
        /// </summary>
        /// <param name="operation">What the lock is for, recorded with the wait time (<c>vertex.project_lock.wait</c>).</param>
        public static async Task<long> LockProjectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, string operation, CancellationToken ct)
        {
            using VertexMetrics.Timer wait = VertexMetrics.Time(VertexMetrics.ProjectLockWait, "operation", operation);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(LockProjectSql, new { ProjectId = projectId }, transaction, cancellationToken: ct));
        }

        /// <summary>
        /// Locks the project's counters row. <c>FOR NO KEY UPDATE</c> is the weakest lock that serializes writers; unlike
        /// <c>FOR UPDATE</c> it doesn't block foreign-key checks against the row.
        /// </summary>
        public const string LockProjectSql = "SELECT event_seq FROM projects.counters WHERE project_id = @ProjectId FOR NO KEY UPDATE;";

        /// <summary>
        /// Records an event in the caller's transaction.
        /// </summary>
        /// <returns>The event's seq.</returns>
        public static async Task<long> AppendAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, NewEvent newEvent, CancellationToken ct)
        {
            await using NpgsqlCommand command = new(AppendSql, connection, transaction);
            AddAppendParameters(command.Parameters, projectId, newEvent);
            return (long)(await command.ExecuteScalarAsync(ct))!;
        }

        /// <summary>
        /// The same as <see cref="AppendAsync"/> as a command for an <see cref="NpgsqlBatch"/>, so a writer can record its
        /// events in the same round trip as its changes. The command returns the event's seq.
        /// </summary>
        public static NpgsqlBatchCommand CreateAppendCommand(Guid projectId, NewEvent newEvent)
        {
            NpgsqlBatchCommand command = new(AppendSql);
            AddAppendParameters(command.Parameters, projectId, newEvent);
            return command;
        }

        /// <summary>
        /// Takes the next seq and inserts the event in one statement. The caller must already hold the counters row lock
        /// (<see cref="LockProjectAsync"/>), or this statement takes it.
        /// </summary>
        private const string AppendSql = "WITH next AS (UPDATE projects.counters SET event_seq = event_seq + 1 WHERE project_id = @ProjectId RETURNING event_seq) " +
                                         "INSERT INTO events.events (project_id, seq, type, branch_id, user_id, workspace_id, host, payload, created_at) " +
                                         "SELECT @ProjectId, next.event_seq, @Type, @BranchId, @UserId, @WorkspaceId, @Host, CAST(@Payload AS JSONB), @CreatedAt FROM next RETURNING seq;";

        private static void AddAppendParameters(NpgsqlParameterCollection parameters, Guid projectId, NewEvent newEvent)
        {
            parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
            parameters.Add(new NpgsqlParameter<string>("Type", newEvent.Type));
            parameters.Add(new NpgsqlParameter("BranchId", NpgsqlDbType.Uuid) { Value = (object?)newEvent.BranchId ?? DBNull.Value });
            parameters.Add(new NpgsqlParameter<Guid>("UserId", newEvent.UserId));
            parameters.Add(new NpgsqlParameter("WorkspaceId", NpgsqlDbType.Uuid) { Value = (object?)newEvent.WorkspaceId ?? DBNull.Value });
            parameters.Add(new NpgsqlParameter("Host", NpgsqlDbType.Text) { Value = (object?)newEvent.Host ?? DBNull.Value });
            parameters.Add(new NpgsqlParameter<string>("Payload", JsonSerializer.Serialize(newEvent.Payload, newEvent.Payload.GetType(), ProjectEvent.Json)));
            parameters.Add(new NpgsqlParameter<long>("CreatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        /// <summary>
        /// Events after <paramref name="afterSeq"/>, in order, at most <paramref name="limit"/>.
        /// </summary>
        public async Task<IReadOnlyList<ProjectEvent>> ReadAsync(Guid projectId, long afterSeq, int limit, CancellationToken ct)
        {
            const string sql = "SELECT " + Columns + " FROM events.events e JOIN auth.users u ON u.id = e.user_id " +
                               "WHERE e.project_id = @ProjectId AND e.seq > @After ORDER BY e.seq LIMIT @Limit;";

            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return (await connection.QueryAsync<ProjectEvent>(new CommandDefinition(sql, new { ProjectId = projectId, After = afterSeq, Limit = limit }, cancellationToken: ct))).ToArray();
        }

        /// <summary>
        /// Like <see cref="ReadAsync"/> inside the caller's transaction, for readers that need one snapshot.
        /// </summary>
        public static async Task<IReadOnlyList<ProjectEvent>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, long afterSeq,
            long upToSeq, string typePrefix, Guid? branchId, CancellationToken ct)
        {
            const string sql = "SELECT " + Columns + " FROM events.events e JOIN auth.users u ON u.id = e.user_id " +
                               "WHERE e.project_id = @ProjectId AND e.seq > @After AND e.seq <= @UpTo AND e.type LIKE @Prefix " +
                               "AND (CAST(@BranchId AS UUID) IS NULL OR e.branch_id = @BranchId) ORDER BY e.seq;";

            return (await connection.QueryAsync<ProjectEvent>(new CommandDefinition(sql,
                new { ProjectId = projectId, After = afterSeq, UpTo = upToSeq, Prefix = typePrefix + "%", BranchId = branchId }, transaction, cancellationToken: ct))).ToArray();
        }

        /// <summary>
        /// The project's latest event seq (0 when nothing happened yet).
        /// </summary>
        public async Task<long> GetLatestSeqAsync(Guid projectId, CancellationToken ct)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT event_seq FROM projects.counters WHERE project_id = @ProjectId;",
                new { ProjectId = projectId }, cancellationToken: ct));
        }

        /// <summary>
        /// True when event <paramref name="seq"/> is still kept (not pruned).
        /// </summary>
        public static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid projectId, long seq, CancellationToken ct)
        {
            return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM events.events WHERE project_id = @ProjectId AND seq = @Seq);",
                new { ProjectId = projectId, Seq = seq }, transaction, cancellationToken: ct));
        }

        public async Task<bool> ExistsAsync(Guid projectId, long seq, CancellationToken ct)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await ExistsAsync(connection, null, projectId, seq, ct);
        }

        /// <summary>
        /// Deletes events created before <paramref name="before"/> (Unix milliseconds).
        /// </summary>
        public async Task<int> PruneAsync(long before, CancellationToken ct)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            return await connection.ExecuteAsync(new CommandDefinition("DELETE FROM events.events WHERE created_at < @Before;", new { Before = before }, cancellationToken: ct));
        }

        /// <summary>
        /// Opens a connection to the database, for readers that run their own transaction (the event dispatcher).
        /// </summary>
        public NpgsqlConnection CreateConnection()
        {
            return (NpgsqlConnection)_database.CreateConnection();
        }
    }
}
