// Copyright 2026 Entex Interactive

using Dapper;
using Npgsql;

namespace Vertex.Server.Events
{
    /// <summary>
    /// Something on the server that reacts to project events: a build trigger, a Slack or email plugin. Register one in
    /// DI and <see cref="EventDispatcher"/> hands it every matching event, in order, at least once.
    /// </summary>
    public interface IEventHandler
    {
        /// <summary>A stable name; its progress is stored under it, so renaming it starts it over from now.</summary>
        string Name { get; }

        bool Handles(string type);

        /// <summary>
        /// Handles one event. Throwing leaves the event unhandled; it's retried later, so make handling repeatable.
        /// </summary>
        Task HandleAsync(ProjectEvent projectEvent, CancellationToken ct);
    }

    /// <summary>
    /// Runs the registered <see cref="IEventHandler"/>s over new project events.
    /// </summary>
    /// <remarks>
    /// Each handler has a cursor per project in <c>events.cursors</c>. A batch is handled inside a transaction that
    /// holds the cursor row (<c>FOR UPDATE SKIP LOCKED</c>), so across any number of server instances one handles it
    /// and the others skip it, and a crash before commit means the batch is handled again. A new handler starts at the
    /// current events instead of replaying history. It wakes on every notification, and every 30 s anyway.
    /// </remarks>
    public sealed class EventDispatcher : BackgroundService
    {
        private const int BatchSize = 500;
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

        private readonly ILogger<EventDispatcher> _logger;
        private readonly IReadOnlyList<IEventHandler> _handlers;
        private readonly EventStore _store;
        private readonly INotificationService _notifications;

        public EventDispatcher(ILogger<EventDispatcher> logger, IEnumerable<IEventHandler> handlers, EventStore store, INotificationService notifications)
        {
            _logger = logger;
            _handlers = handlers.ToList();
            _store = store;
            _notifications = notifications;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_handlers.Count == 0) return;
            _logger.LogInformation("Dispatching project events to {Handlers}", string.Join(", ", _handlers.Select(h => h.Name)));

            while (!stoppingToken.IsCancellationRequested)
            {
                Task wake = _notifications.WaitForAnyAsync(stoppingToken);
                foreach (IEventHandler handler in _handlers)
                {
                    try
                    {
                        await RunAsync(handler, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Event handler {Handler} failed; it will retry", handler.Name);
                    }
                }

                try
                {
                    await Task.WhenAny(wake, Task.Delay(IdleInterval, stoppingToken));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task RunAsync(IEventHandler handler, CancellationToken ct)
        {
            await using NpgsqlConnection connection = _store.CreateConnection();
            await connection.OpenAsync(ct);

            // New projects (and new handlers) start at the current event, so history isn't replayed.
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO events.cursors (handler, project_id, seq) SELECT @Handler, p.id, p.event_seq FROM projects.projects p ON CONFLICT DO NOTHING;",
                new { Handler = handler.Name }, cancellationToken: ct));

            Guid[] pending = (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT c.project_id FROM events.cursors c JOIN projects.projects p ON p.id = c.project_id WHERE c.handler = @Handler AND p.event_seq > c.seq;",
                new { Handler = handler.Name }, cancellationToken: ct))).ToArray();

            foreach (Guid projectId in pending)
            {
                while (await RunBatchAsync(connection, handler, projectId, ct))
                {
                    // Keep going while full batches come back.
                }
            }
        }

        /// <returns>True when a full batch was handled and there may be more.</returns>
        private async Task<bool> RunBatchAsync(NpgsqlConnection connection, IEventHandler handler, Guid projectId, CancellationToken ct)
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
            long? cursor = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT seq FROM events.cursors WHERE handler = @Handler AND project_id = @ProjectId FOR UPDATE SKIP LOCKED;",
                new { Handler = handler.Name, ProjectId = projectId }, transaction, cancellationToken: ct));

            if (cursor is null) return false; // Another instance has it.

            // Seqs are gapless, so the next batch is a seq range.
            IReadOnlyList<ProjectEvent> batch = await EventStore.ReadAsync(connection, transaction, projectId, cursor.Value, cursor.Value + BatchSize, string.Empty, null, ct);
            if (batch.Count == 0)
            {
                // Events after the cursor were pruned (the handler was down for longer than they're kept): skip ahead.
                long? next = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT MIN(seq) FROM events.events WHERE project_id = @ProjectId AND seq > @Seq;", new { ProjectId = projectId, Seq = cursor.Value }, transaction, cancellationToken: ct));

                if (next is null) return false;
                _logger.LogWarning("Event handler {Handler} missed pruned events {From}-{To} in project {ProjectId}", handler.Name, cursor.Value + 1, next.Value - 1, projectId);
                await connection.ExecuteAsync(new CommandDefinition("UPDATE events.cursors SET seq = @Seq WHERE handler = @Handler AND project_id = @ProjectId;",
                    new { Seq = next.Value - 1, Handler = handler.Name, ProjectId = projectId }, transaction, cancellationToken: ct));

                await transaction.CommitAsync(ct);
                return true;
            }

            foreach (ProjectEvent projectEvent in batch.Where(e => handler.Handles(e.Type)))
                await handler.HandleAsync(projectEvent, ct);

            await connection.ExecuteAsync(new CommandDefinition("UPDATE events.cursors SET seq = @Seq WHERE handler = @Handler AND project_id = @ProjectId;",
                new { Seq = batch[^1].Seq, Handler = handler.Name, ProjectId = projectId }, transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);
            return batch.Count == BatchSize;
        }
    }

    /// <summary>
    /// Logs every project event; registered in Development to show the dispatcher working.
    /// </summary>
    public sealed class LoggingEventHandler(ILogger<LoggingEventHandler> logger) : IEventHandler
    {
        public string Name => "log";

        public bool Handles(string type)
        {
            return true;
        }

        public Task HandleAsync(ProjectEvent projectEvent, CancellationToken ct)
        {
            logger.LogInformation("Event #{Seq} {Type} in project {ProjectId} by {User}: {Payload}", projectEvent.Seq, projectEvent.Type, projectEvent.ProjectId,
                projectEvent.Username, projectEvent.Payload.Length > 200 ? projectEvent.Payload[..200] + "..." : projectEvent.Payload);
            return Task.CompletedTask;
        }
    }
}
