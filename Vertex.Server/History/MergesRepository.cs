// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Dapper;
using Npgsql;
using Vertex.Server.Database;
using Vertex.Server.Events;
using Vertex.Server.Telemetry;

namespace Vertex.Server.History
{
    /// <summary>
    /// What a merge does with files changed on both branches.
    /// </summary>
    public enum MergeResolve
    {
        /// <summary>Any conflict refuses the merge.</summary>
        None,

        /// <summary>Conflicting files keep the target's content.</summary>
        KeepTarget,

        /// <summary>Conflicting files take the source's content.</summary>
        TakeSource
    }

    public enum MergeStatus
    {
        /// <summary>The target took the source's changes (in <see cref="MergeOutcome.Revision"/>, or none were needed).</summary>
        Merged,

        /// <summary>Nothing changed on the source since the last merge.</summary>
        UpToDate,

        /// <summary>Files changed on both branches; nothing was written.</summary>
        Conflicts,

        /// <summary>Someone holds a lock on the target for a file the merge would change; nothing was written.</summary>
        Locked,

        /// <summary>The branches share no ancestor (one of them started blank).</summary>
        Unrelated
    }

    /// <summary>
    /// A file changed on both branches since their merge base. A null id means the branch deleted the file (or, for
    /// the target, never had it).
    /// </summary>
    public sealed record MergeConflictRow(string Path, string? SourceFileId, string? TargetFileId);

    /// <param name="Status">What happened.</param>
    /// <param name="Revision">The revision written on the target, or null when the merge wrote none.</param>
    /// <param name="Files">Files the revision changed.</param>
    /// <param name="Conflicts">For <see cref="MergeStatus.Conflicts"/>: every file changed on both branches.</param>
    /// <param name="Resolved">Conflicts settled by the requested <see cref="MergeResolve"/>.</param>
    /// <param name="Locks">For <see cref="MergeStatus.Locked"/>: the target's locks on files the merge would change.</param>
    /// <param name="EventSeq">The project's event seq after the merge's events, or null when none were recorded.</param>
    public sealed record MergeOutcome(MergeStatus Status, long? Revision = null, int Files = 0, IReadOnlyList<MergeConflictRow>? Conflicts = null,
        int Resolved = 0, IReadOnlyList<LockRow>? Locks = null, long? EventSeq = null);

    /// <summary>
    /// Merges between any two branches that share an ancestor, as one revision on the target. Works on file ids
    /// only: it never reads or merges content.
    /// </summary>
    /// <remarks>
    /// For every path whose content differs between the two branches, with B its file id at the merge base, S on the
    /// source and T on the target: S == B needs nothing (only the target changed it), T == B takes S, and anything else
    /// is a conflict. The merge base is the newest state both branches contain: the source side of the latest merge
    /// between them in either direction (<c>revisions.merges</c>), or, before any, their nearest common ancestor branch
    /// as of the older of the two copies (the parent for a parent and child, the branch both were cut from for siblings). Every merge is complete (conflicts refuse it or are settled), so whatever a merge settled stays settled.
    /// <para>
    /// The plan is read without holding the project's counters row, because it can read a lot of history. The write
    /// then takes the row, checks in the same round trip that neither branch got a revision and no merge between them
    /// was recorded since the plan was read, and starts over if one was.
    /// </para>
    /// </remarks>
    public class MergesRepository
    {
        /// <summary>Plans read again when the branches keep changing between the plan and the write.</summary>
        private const int Attempts = 5;

        /// <summary>Conflicting paths named in a refused merge's event.</summary>
        private const int EventPaths = 20;

        /// <summary>
        /// One row for the pair: how the branches are related, their heads, and the merges between them. Read for the
        /// plan and again under the counters row to detect anything that changed in between.
        /// </summary>
        private const string PairSql =
            "SELECT s.parent_id AS SourceParent, t.parent_id AS TargetParent, s.base_revision AS SourceBase, t.base_revision AS TargetBase, " +
            "GREATEST(s.base_revision, COALESCE((SELECT MAX(r.number) FROM revisions.revisions r WHERE r.branch_id = s.id), 0)) AS SourceHead, " +
            "GREATEST(t.base_revision, COALESCE((SELECT MAX(r.number) FROM revisions.revisions r WHERE r.branch_id = t.id), 0)) AS TargetHead, " +
            "m.id AS LastMergeId, m.source_branch_id AS LastMergeBranch, m.source_revision AS LastMergeRevision " +
            "FROM branches.branches s CROSS JOIN branches.branches t " +
            "LEFT JOIN LATERAL (SELECT x.id, x.source_branch_id, x.source_revision FROM revisions.merges x " +
            "  WHERE (x.source_branch_id = s.id AND x.target_branch_id = t.id) OR (x.source_branch_id = t.id AND x.target_branch_id = s.id) " +
            "  ORDER BY x.id DESC LIMIT 1) m ON TRUE " +
            "WHERE s.id = @Source AND t.id = @Target AND s.project_id = @ProjectId AND t.project_id = @ProjectId;";

        /// <summary>
        /// The nearest branch both branches descend from (or one of them, when it's the other's ancestor) and the
        /// newest revision of it both contain: each branch's chain of parents, with how far into each parent it reaches
        /// (its own head for itself, then the revision it was copied at), joined on the first branch they share.
        /// </summary>
        private const string AncestorSql =
            "WITH RECURSIVE a AS (" +
            "  SELECT id, parent_id, base_revision, @SourceHead::BIGINT AS upto, 0 AS depth FROM branches.branches WHERE id = @Source " +
            "  UNION ALL " +
            "  SELECT p.id, p.parent_id, p.base_revision, LEAST(l.upto, l.base_revision), l.depth + 1 " +
            "  FROM a l JOIN branches.branches p ON p.id = l.parent_id WHERE l.depth < 64" +
            "), b AS (" +
            "  SELECT id, parent_id, base_revision, @TargetHead::BIGINT AS upto, 0 AS depth FROM branches.branches WHERE id = @Target " +
            "  UNION ALL " +
            "  SELECT p.id, p.parent_id, p.base_revision, LEAST(l.upto, l.base_revision), l.depth + 1 " +
            "  FROM b l JOIN branches.branches p ON p.id = l.parent_id WHERE l.depth < 64" +
            ") " +
            "SELECT a.id AS BranchId, LEAST(a.upto, b.upto) AS Revision FROM a JOIN b ON b.id = a.id ORDER BY a.depth LIMIT 1;";

        /// <summary>
        /// Every path whose content differs between the branches, with its file id at the base (the files of
        /// @BaseBranch at @Base, rebuilt from history along its parent chain), on the source and on the target.
        /// </summary>
        /// <remarks>
        /// Comparing the two heads is one join over both branches' head rows, with no history; only the differing
        /// paths are then looked up in history.
        /// </remarks>
        private const string FilesSql =
            "WITH RECURSIVE diff AS (" +
            "  SELECT COALESCE(s.path_key, t.path_key) AS path_key, COALESCE(s.path, t.path) AS path, s.file_id AS source_id, t.file_id AS target_id " +
            "  FROM (SELECT path_key, path, file_id FROM revisions.head WHERE branch_id = @Source) s " +
            "  FULL JOIN (SELECT path_key, path, file_id FROM revisions.head WHERE branch_id = @Target) t ON t.path_key = s.path_key " +
            "  WHERE s.file_id IS DISTINCT FROM t.file_id" +
            "), lineage AS (" +
            "  SELECT id, parent_id, base_revision, @Base::BIGINT AS upto FROM branches.branches WHERE id = @BaseBranch " +
            "  UNION ALL " +
            "  SELECT p.id, p.parent_id, p.base_revision, LEAST(l.upto, l.base_revision) " +
            "  FROM lineage l JOIN branches.branches p ON p.id = l.parent_id WHERE l.base_revision > 0" +
            "), based AS (" +
            "  SELECT DISTINCT ON (lower(c.path)) lower(c.path) AS path_key, c.file_id " +
            "  FROM lineage l JOIN revisions.revisions r ON r.branch_id = l.id AND r.number <= l.upto " +
            "  JOIN revisions.changes c ON c.revision_id = r.id " +
            "  WHERE lower(c.path) IN (SELECT path_key FROM diff) " +
            "  ORDER BY lower(c.path), r.number DESC" +
            ") " +
            "SELECT d.path_key AS PathKey, d.path AS Path, b.file_id AS BaseFileId, d.source_id AS SourceFileId, d.target_id AS TargetFileId " +
            "FROM diff d LEFT JOIN based b ON b.path_key = d.path_key " +
            "ORDER BY d.path_key;";

        private readonly PostgresDatabase _database;

        public MergesRepository(PostgresDatabase database)
        {
            _database = database;
        }

        /// <summary>
        /// Merges the changes made on <paramref name="sourceId"/> since the last merge into <paramref name="targetId"/>
        /// as one revision, or reports why it can't. A merge that only settled conflicts in the target's favor writes no
        /// revision but is still recorded, so the next merge doesn't ask again.
        /// </summary>
        public async Task<MergeOutcome> MergeAsync(Guid projectId, Guid sourceId, Guid targetId, Guid userId, Guid? workspaceId, string? host, string message,
            MergeResolve resolve, CancellationToken cancellationToken, bool automatic = false)
        {
            for (int attempt = 1; ; attempt++)
            {
                await using NpgsqlConnection connection = _database.CreateConnection();
                await connection.OpenAsync(cancellationToken);

                BranchPair? pair = await connection.QuerySingleOrDefaultAsync<BranchPair>(new CommandDefinition(PairSql,
                    new { ProjectId = projectId, Source = sourceId, Target = targetId }, cancellationToken: cancellationToken));
                if (pair is null) throw new ArgumentException("A branch doesn't exist in this project.");

                // The newest state both branches contain: the source side of their latest merge, else their nearest
                // common ancestor as of the older of the two copies (a parent, or the branch both were cut from).
                Guid baseBranch;
                long baseRevision;
                if (pair.LastMergeBranch is Guid merged)
                {
                    (baseBranch, baseRevision) = (merged, pair.LastMergeRevision!.Value);
                }
                else
                {
                    AncestorRow? ancestor = await connection.QuerySingleOrDefaultAsync<AncestorRow>(new CommandDefinition(AncestorSql,
                        new { Source = sourceId, Target = targetId, pair.SourceHead, pair.TargetHead }, cancellationToken: cancellationToken));
                    if (ancestor is null) return new MergeOutcome(MergeStatus.Unrelated);
                    (baseBranch, baseRevision) = (ancestor.BranchId, ancestor.Revision);
                }

                IReadOnlyList<MergeFileRow> files = (await connection.QueryAsync<MergeFileRow>(new CommandDefinition(FilesSql,
                    new { Source = sourceId, Target = targetId, BaseBranch = baseBranch, Base = baseRevision }, commandTimeout: 300, cancellationToken: cancellationToken))).ToReadOnly();

                MergeOutcome? outcome = await WriteAsync(connection, projectId, sourceId, targetId, userId, workspaceId, host, message, resolve, automatic, pair, files,
                    cancellationToken);
                if (outcome is not null) return outcome;
                if (attempt == Attempts) throw new TimeoutException("The branches kept changing while merging. Try again in a moment.");
            }
        }

        /// <summary>
        /// Classifies the planned files and writes the merge under the project's counters row.
        /// </summary>
        /// <returns>Null when either branch changed since the plan was read.</returns>
        private static async Task<MergeOutcome?> WriteAsync(NpgsqlConnection connection, Guid projectId, Guid sourceId, Guid targetId, Guid userId, Guid? workspaceId,
            string? host, string message, MergeResolve resolve, bool automatic, BranchPair pair, IReadOnlyList<MergeFileRow> files, CancellationToken cancellationToken)
        {
            List<(SubmitChange Change, short Action)> applied = new();
            List<MergeConflictRow> conflicts = new();
            int resolved = 0;
            foreach (MergeFileRow file in files)
            {
                if (Same(file.SourceFileId, file.BaseFileId) || Same(file.SourceFileId, file.TargetFileId)) continue;
                if (!Same(file.TargetFileId, file.BaseFileId))
                {
                    if (resolve == MergeResolve.None)
                    {
                        conflicts.Add(new MergeConflictRow(file.Path, file.SourceFileId, file.TargetFileId));
                        continue;
                    }

                    resolved++;
                    if (resolve == MergeResolve.KeepTarget) continue;
                }

                short action = file.TargetFileId is null ? (short)0 : file.SourceFileId is null ? (short)2 : (short)1;
                applied.Add((new SubmitChange(file.Path, file.PathKey, file.SourceFileId, file.TargetFileId), action));
            }

            // Only the target changed things since the base: it already has everything, and there's nothing to record.
            if (applied.Count == 0 && conflicts.Count == 0 && resolved == 0) return new MergeOutcome(MergeStatus.UpToDate);

            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            BranchPair? current;
            List<LockRow> locks = new();
            long locked;
            await using (NpgsqlBatch read = new(connection, transaction))
            {
                NpgsqlBatchCommand lockCommand = new(EventStore.LockProjectSql);
                lockCommand.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                read.BatchCommands.Add(lockCommand);

                NpgsqlBatchCommand pairCommand = new(PairSql);
                pairCommand.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                pairCommand.Parameters.Add(new NpgsqlParameter<Guid>("Source", sourceId));
                pairCommand.Parameters.Add(new NpgsqlParameter<Guid>("Target", targetId));
                read.BatchCommands.Add(pairCommand);

                // A file someone is editing on the target can't be changed under them.
                NpgsqlBatchCommand locksCommand = new(LocksRepository.ReadLocksSql);
                locksCommand.Parameters.Add(new NpgsqlParameter<Guid>("BranchId", targetId));
                locksCommand.Parameters.Add(new NpgsqlParameter<string[]>("Keys", applied.Select(a => a.Change.PathKey).ToArray()));
                read.BatchCommands.Add(locksCommand);

                using (VertexMetrics.Time(VertexMetrics.ProjectLockWait, "operation", "merge"))
                {
                    await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken);
                    locked = Stopwatch.GetTimestamp();
                    await reader.NextResultAsync(cancellationToken);
                    current = await reader.ReadAsync(cancellationToken)
                        ? new BranchPair(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3),
                            reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetGuid(7),
                            reader.IsDBNull(8) ? null : reader.GetInt64(8))
                        : null;
                    await reader.NextResultAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        locks.Add(new LockRow(reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
                }
            }

            if (current is null) throw new ArgumentException("A branch doesn't exist in this project.");
            if (current.SourceHead != pair.SourceHead || current.TargetHead != pair.TargetHead || current.LastMergeId != pair.LastMergeId) return null;
            if (locks.Count > 0) return new MergeOutcome(MergeStatus.Locked, Locks: locks);

            if (conflicts.Count > 0)
            {
                // Nothing is merged, but people following the target should hear the merge was tried.
                long refusedSeq = await EventStore.AppendAsync(connection, transaction, projectId, new NewEvent(EventTypes.MergeRefused, targetId, userId, workspaceId, host,
                    new MergePayload(sourceId, pair.SourceHead, null, 0, conflicts.Take(EventPaths).Select(c => c.Path).ToArray(), automatic)), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new MergeOutcome(MergeStatus.Conflicts, Conflicts: conflicts, EventSeq: refusedSeq);
            }

            long? number = null;
            if (applied.Count > 0)
            {
                await using NpgsqlCommand numberCommand = new("UPDATE projects.counters SET revision_counter = revision_counter + 1 WHERE project_id = @ProjectId RETURNING revision_counter;",
                    connection, transaction);
                numberCommand.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                number = (long)(await numberCommand.ExecuteScalarAsync(cancellationToken))!;
            }

            long eventSeq = 0;
            await using (NpgsqlBatch write = new(connection, transaction))
            {
                if (number is long revision)
                {
                    RevisionsRepository.AddRevisionCommands(write, projectId, targetId, revision, userId, message, applied);
                    write.BatchCommands.Add(EventStore.CreateAppendCommand(projectId,
                        new NewEvent(EventTypes.RevisionCreated, targetId, userId, workspaceId, host, new RevisionPayload(revision, message, applied.Count))));
                }

                NpgsqlBatchCommand record = new("INSERT INTO revisions.merges (project_id, source_branch_id, target_branch_id, source_revision, target_revision, user_id, created_at) " +
                                                "VALUES (@ProjectId, @Source, @Target, @SourceRevision, @TargetRevision, @UserId, @CreatedAt);");
                record.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                record.Parameters.Add(new NpgsqlParameter<Guid>("Source", sourceId));
                record.Parameters.Add(new NpgsqlParameter<Guid>("Target", targetId));
                record.Parameters.Add(new NpgsqlParameter<long>("SourceRevision", pair.SourceHead));
                record.Parameters.Add(new NpgsqlParameter("TargetRevision", (object?)number ?? DBNull.Value));
                record.Parameters.Add(new NpgsqlParameter<Guid>("UserId", userId));
                record.Parameters.Add(new NpgsqlParameter<long>("CreatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                write.BatchCommands.Add(record);

                write.BatchCommands.Add(EventStore.CreateAppendCommand(projectId, new NewEvent(EventTypes.MergeCompleted, targetId, userId, workspaceId, host,
                    new MergePayload(sourceId, pair.SourceHead, number, applied.Count, [], automatic))));

                await using NpgsqlDataReader reader = await write.ExecuteReaderAsync(cancellationToken);
                do
                {
                    while (await reader.ReadAsync(cancellationToken)) eventSeq = reader.GetInt64(0);
                } while (await reader.NextResultAsync(cancellationToken));
            }

            long commit = Stopwatch.GetTimestamp();
            await transaction.CommitAsync(cancellationToken);
            long done = Stopwatch.GetTimestamp();
            VertexMetrics.CommitDuration.Record(Stopwatch.GetElapsedTime(commit, done).TotalSeconds, VertexMetrics.Tag("operation", "merge"));
            VertexMetrics.ProjectLockHeld.Record(Stopwatch.GetElapsedTime(locked, done).TotalSeconds, VertexMetrics.Tag("operation", "merge"));
            return new MergeOutcome(MergeStatus.Merged, number, applied.Count, Resolved: resolved, EventSeq: eventSeq);
        }

        private static bool Same(string? a, string? b)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        /// <param name="LastMergeId">The latest merge between the two in either direction, or null when there was none.</param>
        /// <param name="LastMergeBranch">That merge's source branch.</param>
        /// <param name="LastMergeRevision">That merge's source revision.</param>
        private sealed record BranchPair(Guid? SourceParent, Guid? TargetParent, long SourceBase, long TargetBase, long SourceHead, long TargetHead, long? LastMergeId,
            Guid? LastMergeBranch, long? LastMergeRevision);

        private sealed record AncestorRow(Guid BranchId, long Revision);

        private sealed record MergeFileRow(string PathKey, string Path, string? BaseFileId, string? SourceFileId, string? TargetFileId);
    }
}
