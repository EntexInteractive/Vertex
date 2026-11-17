// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;
using Vertex.Server.Database;
using Vertex.Server.Events;
using Vertex.Server.Telemetry;

namespace Vertex.Server.History
{
    /// <summary>
    /// Runs lock and unlock requests so that the ones arriving together for a project share one transaction and one
    /// commit.
    /// </summary>
    /// <remarks>
    /// Writes to a project serialize on its counters row, and each one holds that row through its commit, so a project
    /// can do at most one write per commit latency. On storage where a durable commit is slow (ZFS without a separate
    /// log device, network disks) that ceiling is low. Here each instance keeps at most one lock transaction in flight
    /// per project; requests that arrive meanwhile wait and then all run in the next transaction (up to
    /// <see cref="MaximumBatch"/>), each as one statement with its own result and its own event seq, under one row lock
    /// and one commit. A request alone costs the same round trips as before; under load, throughput grows with the
    /// batch instead of being capped by commit latency.
    ///
    /// Other instances and submits still take the same row lock, so correctness doesn't depend on this queue. If a
    /// shared transaction fails on a statement error (say, a branch deleted between the access check and the write),
    /// its requests are retried one by one so only the failing one reports the error.
    /// </remarks>
    public sealed class ProjectWriteQueue
    {
        /// <summary>Most requests in one transaction.</summary>
        public const int MaximumBatch = 64;

        private readonly ILogger<ProjectWriteQueue> _logger;
        private readonly PostgresDatabase _database;
        private readonly ConcurrentDictionary<Guid, ProjectQueue> _queues = new();

        public ProjectWriteQueue(ILogger<ProjectWriteQueue> logger, ServerSettings settings)
        {
            _logger = logger;
            _database = new PostgresDatabase(settings);
        }

        /// <summary>
        /// Locks every free path of the request and reports who holds the rest.
        /// </summary>
        public Task<LockOutcome> LockAsync(LockWrite write, CancellationToken ct)
        {
            LockRequest request = new(write, ct);
            Enqueue(write.ProjectId, request);
            return request.Result.Task.WaitAsync(ct);
        }

        /// <summary>
        /// Releases the request's locks.
        /// </summary>
        public Task<UnlockOutcome> UnlockAsync(UnlockWrite write, CancellationToken ct)
        {
            UnlockRequest request = new(write, ct);
            Enqueue(write.ProjectId, request);
            return request.Result.Task.WaitAsync(ct);
        }

        private void Enqueue(Guid projectId, PendingWrite request)
        {
            ProjectQueue queue = _queues.GetOrAdd(projectId, _ => new ProjectQueue());
            bool start;
            lock (queue.Gate)
            {
                queue.Pending.Add(request);
                start = !queue.Running;
                queue.Running = true;
            }

            if (start) _ = Task.Run(() => DrainAsync(projectId, queue));
        }

        /// <summary>
        /// Runs the project's queued requests, a batch at a time, until none are left.
        /// </summary>
        private async Task DrainAsync(Guid projectId, ProjectQueue queue)
        {
            while (true)
            {
                List<PendingWrite> batch;
                lock (queue.Gate)
                {
                    if (queue.Pending.Count == 0)
                    {
                        queue.Running = false;
                        return;
                    }

                    int count = Math.Min(MaximumBatch, queue.Pending.Count);
                    batch = queue.Pending.GetRange(0, count);
                    queue.Pending.RemoveRange(0, count);
                }

                // A caller that gave up while queued gets nothing done on its behalf.
                batch.RemoveAll(request =>
                {
                    if (!request.Cancellation.IsCancellationRequested) return false;
                    request.Cancel();
                    return true;
                });

                if (batch.Count == 0) continue;

                try
                {
                    await RunAsync(projectId, batch);
                }
                catch (PostgresException ex) when (batch.Count > 1)
                {
                    _logger.LogWarning(ex, "A shared lock transaction of {Count} requests in project {ProjectId} failed; retrying them one by one", batch.Count, projectId);
                    foreach (PendingWrite request in batch)
                    {
                        try
                        {
                            await RunAsync(projectId, [request]);
                        }
                        catch (Exception single)
                        {
                            request.Fail(single);
                        }
                    }
                }
                catch (Exception ex)
                {
                    foreach (PendingWrite request in batch) request.Fail(ex);
                }
            }
        }

        /// <summary>
        /// One transaction: take the project's counters row, run every request's statement in one round trip, commit,
        /// then hand each caller its result.
        /// </summary>
        private async Task RunAsync(Guid projectId, IReadOnlyList<PendingWrite> requests)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

            await using (NpgsqlCommand lockCommand = new(EventStore.LockProjectSql, connection, transaction))
            {
                lockCommand.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                await lockCommand.ExecuteScalarAsync();
            }

            // Waiting covers the time queued on this instance as well as the row lock itself.
            long locked = Stopwatch.GetTimestamp();
            foreach (PendingWrite request in requests)
                VertexMetrics.ProjectLockWait.Record(Stopwatch.GetElapsedTime(request.EnqueuedAt, locked).TotalSeconds, VertexMetrics.Tag("operation", request.Operation));

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using (NpgsqlBatch batch = new(connection, transaction))
            {
                foreach (PendingWrite request in requests) batch.BatchCommands.Add(request.CreateCommand(now));

                await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync();
                for (int i = 0; i < requests.Count; i++)
                {
                    if (i > 0) await reader.NextResultAsync();
                    await requests[i].ReadAsync(reader);
                }
            }

            long commit = Stopwatch.GetTimestamp();
            await transaction.CommitAsync();
            long done = Stopwatch.GetTimestamp();

            VertexMetrics.CommitDuration.Record(Stopwatch.GetElapsedTime(commit, done).TotalSeconds, VertexMetrics.Tag("operation", "locks"));
            VertexMetrics.ProjectLockHeld.Record(Stopwatch.GetElapsedTime(locked, done).TotalSeconds, VertexMetrics.Tag("operation", "locks"));
            VertexMetrics.WriteBatchSize.Record(requests.Count);
            foreach (PendingWrite request in requests) request.Complete();
        }

        private sealed class ProjectQueue
        {
            public readonly Lock Gate = new();
            public readonly List<PendingWrite> Pending = new();
            public bool Running;
        }

        /// <summary>
        /// A queued request: builds its statement, reads its result set, and completes its caller after the commit.
        /// </summary>
        private abstract class PendingWrite(CancellationToken cancellation)
        {
            public long EnqueuedAt { get; } = Stopwatch.GetTimestamp();

            public CancellationToken Cancellation { get; } = cancellation;

            /// <summary>The <c>operation</c> tag for the wait metric.</summary>
            public abstract string Operation { get; }

            public abstract NpgsqlBatchCommand CreateCommand(long now);

            /// <summary>Reads this request's result set. Kept until <see cref="Complete"/>, so nobody sees an uncommitted result.</summary>
            public abstract Task ReadAsync(NpgsqlDataReader reader);

            public abstract void Complete();

            public abstract void Fail(Exception exception);

            public abstract void Cancel();
        }

        private abstract class PendingWrite<T>(CancellationToken cancellation) : PendingWrite(cancellation)
        {
            private T? _value;

            public TaskCompletionSource<T> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected void Set(T value)
            {
                _value = value;
            }

            public override void Complete()
            {
                Result.TrySetResult(_value!);
            }

            public override void Fail(Exception exception)
            {
                Result.TrySetException(exception);
            }

            public override void Cancel()
            {
                Result.TrySetCanceled(Cancellation);
            }
        }

        private sealed class LockRequest(LockWrite write, CancellationToken cancellation) : PendingWrite<LockOutcome>(cancellation)
        {
            public override string Operation => "lock";

            public override NpgsqlBatchCommand CreateCommand(long now)
            {
                return LocksRepository.CreateLockCommand(write, now);
            }

            public override async Task ReadAsync(NpgsqlDataReader reader)
            {
                Set(await LocksRepository.ReadLockAsync(reader, CancellationToken.None));
            }
        }

        private sealed class UnlockRequest(UnlockWrite write, CancellationToken cancellation) : PendingWrite<UnlockOutcome>(cancellation)
        {
            public override string Operation => "unlock";

            public override NpgsqlBatchCommand CreateCommand(long now)
            {
                return LocksRepository.CreateUnlockCommand(write, now);
            }

            public override async Task ReadAsync(NpgsqlDataReader reader)
            {
                Set(await LocksRepository.ReadUnlockAsync(reader, CancellationToken.None));
            }
        }
    }
}
