// Copyright 2026 Entex Interactive

using System.Text.Json;
using Dapper;
using Npgsql;
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
    /// Appending bumps <c>projects.event_seq</c>, which locks the project row until commit: seqs are gapless and commit
    /// in order, so a reader resuming after seq N never skips an event.
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
        /// Locks the project row and returns its current event seq. Transactions that read state before changing it
        /// (locks) take this first, so they serialize per project and never deadlock with each other.
        /// </summary>
        /// <param name="operation">What the lock is for, recorded with the wait time (<c>vertex.project_lock.wait</c>).</param>
        public static async Task<long> LockProjectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, string operation, CancellationToken ct)
        {
            using VertexMetrics.Timer wait = VertexMetrics.Time(VertexMetrics.ProjectLockWait, "operation", operation);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT event_seq FROM projects.projects WHERE id = @ProjectId FOR UPDATE;",
                new { ProjectId = projectId }, transaction, cancellationToken: ct));
        }

        /// <summary>
        /// Records an event in the caller's transaction.
        /// </summary>
        /// <returns>The event's seq.</returns>
        public static async Task<long> AppendAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, NewEvent newEvent, CancellationToken ct)
        {
            long seq = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "UPDATE projects.projects SET event_seq = event_seq + 1 WHERE id = @ProjectId RETURNING event_seq;",
                new { ProjectId = projectId }, transaction, cancellationToken: ct));

            const string sql = "INSERT INTO events.events (project_id, seq, type, branch_id, user_id, workspace_id, host, payload, created_at) " +
                               "VALUES (@ProjectId, @Seq, @Type, @BranchId, @UserId, @WorkspaceId, @Host, CAST(@Payload AS JSONB), @CreatedAt);";

            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                ProjectId = projectId, Seq = seq, newEvent.Type, newEvent.BranchId, newEvent.UserId, newEvent.WorkspaceId, newEvent.Host,
                Payload = JsonSerializer.Serialize(newEvent.Payload, newEvent.Payload.GetType(), ProjectEvent.Json),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, transaction, cancellationToken: ct));

            return seq;
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
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT event_seq FROM projects.projects WHERE id = @ProjectId;",
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
