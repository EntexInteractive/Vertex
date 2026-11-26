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

        /// <summary>The branches share no ancestor (one of them started blank), or, across projects, neither is the
        /// other's upstream.</summary>
        Unrelated,

        /// <summary>A merge into an upstream from a downstream that hasn't taken in the upstream's latest revisions;
        /// nothing was written.</summary>
        Behind
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
    /// <para>
    /// A branch and its upstream (a branch of another project, <c>branches.upstreams</c>) merge the same way, with three
    /// differences. The base is the upstream's state the downstream last took in (<c>revisions.merges.upstream_revision</c>,
    /// read from the upstream's own history), or nothing before the first merge, so the first one copies the upstream's
    /// files and flags files both have with different content. A merge into the upstream only sends the paths the
    /// upstream owns, so the downstream's own files never go up, and only from a downstream that has taken in the
    /// upstream's latest, so every difference left is the downstream's own change. And every merge between them records
    /// the revisions it leaves in step, even when it changed no files, so "behind" is always exact.
    /// </para>
    public class MergesRepository
    {
        /// <summary>Plans read again when the branches keep changing between the plan and the write.</summary>
        private const int Attempts = 5;

        /// <summary>
        /// The paths an upstream owns, as lowercase entries (a folder or a file, matched with <see cref="InScope"/>):
        /// @Paths, plus with @AutoScope the top-level folders and files @ScopeBranch has now.
        /// </summary>
        internal const string ScopeSql =
            "SELECT lower(trim(BOTH '/' FROM p)) AS p FROM unnest(@Paths::TEXT[]) AS p " +
            "UNION " +
            "SELECT split_part(h.path_key, '/', 1) FROM revisions.head h WHERE @AutoScope AND h.branch_id = @ScopeBranch";

        /// <summary>True when the path key <paramref name="key"/> is in the CTE <c>scope</c>: one of its entries, or under one.</summary>
        internal static string InScope(string key)
        {
            return $"EXISTS (SELECT 1 FROM scope WHERE scope.p = {key} OR starts_with({key}, scope.p || '/'))";
        }

        /// <summary>The same test as <see cref="InScope"/>, for one path key against entries from <see cref="ScopeSql"/>.</summary>
        internal static bool IsInScope(string key, IEnumerable<string> entries)
        {
            foreach (string entry in entries)
            {
                string p = entry.Trim('/').ToLowerInvariant();
                if (p.Length == 0) continue;
                if (key == p || (key.Length > p.Length && key[p.Length] == '/' && key.StartsWith(p, StringComparison.Ordinal))) return true;
            }

            return false;
        }

        /// <summary>A merge row (<c>x</c>) between branches <c>s</c> and <c>t</c>, in either direction.</summary>
        private const string Between = "((x.source_branch_id = s.id AND x.target_branch_id = t.id) OR (x.source_branch_id = t.id AND x.target_branch_id = s.id))";

        /// <summary>The latest revisions of branches <c>s</c> and <c>t</c>, as SourceHead and TargetHead.</summary>
        private const string HeadsSql =
            "GREATEST(s.base_revision, COALESCE((SELECT MAX(r.number) FROM revisions.revisions r WHERE r.branch_id = s.id), 0)) AS SourceHead, " +
            "GREATEST(t.base_revision, COALESCE((SELECT MAX(r.number) FROM revisions.revisions r WHERE r.branch_id = t.id), 0)) AS TargetHead";

        /// <summary>
        /// What a plan depends on, re-read under the counters row to detect anything that changed since: both heads and
        /// the latest merge between the two.
        /// </summary>
        private const string StateSql =
            "SELECT " + HeadsSql + ", (SELECT MAX(x.id) FROM revisions.merges x WHERE " + Between + ") AS LastMergeId " +
            "FROM branches.branches s, branches.branches t WHERE s.id = @Source AND t.id = @Target;";

        /// <summary>
        /// A branch and its upstream, in either direction: which way the merge goes, the paths the upstream owns, both
        /// heads, the latest merge between them and the upstream revision the downstream last took in. No row when
        /// neither is the other's upstream.
        /// </summary>
        private const string LinkSql =
            "SELECT (u.branch_id = t.id) AS Pull, u.paths AS Paths, " + HeadsSql + ", " +
            "(SELECT MAX(x.id) FROM revisions.merges x WHERE " + Between + ") AS LastMergeId, " +
            "(SELECT MAX(x.upstream_revision) FROM revisions.merges x WHERE " + Between + ") AS Agreed " +
            "FROM branches.branches s JOIN branches.branches t ON t.id = @Target AND t.project_id = @TargetProject " +
            "JOIN branches.upstreams u ON (u.branch_id = t.id AND u.upstream_id = s.id) OR (u.branch_id = s.id AND u.upstream_id = t.id) " +
            "WHERE s.id = @Source AND s.project_id = @SourceProject;";

        /// <summary>
        /// After a merge within a project, the target has taken in whatever upstream state the source had: for each
        /// upstream both branches share, the source's latest agreement as of @SourceRevision becomes the target's too, as
        /// of @Downstream. A branch made from another inherits it the same way (<see cref="Branches.BranchesRepository"/>).
        /// So taking an engine update on a staging branch and merging that into main leaves main up to date with the
        /// engine as well.
        /// </summary>
        internal const string CarryUpstreamsSql =
            "INSERT INTO revisions.merges (project_id, source_branch_id, target_branch_id, source_revision, target_revision, user_id, created_at, upstream_revision, downstream_revision) " +
            "SELECT @ProjectId, u.upstream_id, @Target, a.revision, NULL, @UserId, @CreatedAt, a.revision, @Downstream " +
            "FROM branches.upstreams u " +
            "CROSS JOIN LATERAL (SELECT MAX(m.upstream_revision) AS revision FROM revisions.merges m " +
            "  WHERE ((m.source_branch_id = u.upstream_id AND m.target_branch_id = @Source) OR (m.source_branch_id = @Source AND m.target_branch_id = u.upstream_id)) " +
            "  AND m.downstream_revision <= @SourceRevision) a " +
            "WHERE u.branch_id = @Target AND a.revision IS NOT NULL " +
            "AND EXISTS (SELECT 1 FROM branches.upstreams o WHERE o.branch_id = @Source AND o.upstream_id = u.upstream_id) " +
            "AND a.revision > COALESCE((SELECT MAX(m.upstream_revision) FROM revisions.merges m " +
            "  WHERE (m.source_branch_id = u.upstream_id AND m.target_branch_id = @Target) OR (m.source_branch_id = @Target AND m.target_branch_id = u.upstream_id)), -1);";

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
        /// <para>
        /// With @Scoped, only paths in the scope (<see cref="ScopeSql"/>, owned by @ScopeBranch) are compared: a merge
        /// into an upstream sends only what it owns.
        /// </para>
        /// </remarks>
        private static readonly string FilesSql =
            "WITH RECURSIVE scope AS (" + ScopeSql + "), diff AS (" +
            "  SELECT COALESCE(s.path_key, t.path_key) AS path_key, COALESCE(s.path, t.path) AS path, s.file_id AS source_id, t.file_id AS target_id " +
            "  FROM (SELECT path_key, path, file_id FROM revisions.head WHERE branch_id = @Source) s " +
            "  FULL JOIN (SELECT path_key, path, file_id FROM revisions.head WHERE branch_id = @Target) t ON t.path_key = s.path_key " +
            "  WHERE s.file_id IS DISTINCT FROM t.file_id AND (NOT @Scoped OR " + InScope("COALESCE(s.path_key, t.path_key)") + ")" +
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
        /// <param name="projectId">The target's project, whose counters row the write takes.</param>
        /// <param name="sourceProjectId">The source's project when it's another one: then one branch must be the other's
        /// upstream (see the class remarks).</param>
        public async Task<MergeOutcome> MergeAsync(Guid projectId, Guid sourceId, Guid targetId, Guid userId, Guid? workspaceId, string? host, string message,
            MergeResolve resolve, CancellationToken cancellationToken, bool automatic = false, Guid? sourceProjectId = null)
        {
            if (sourceProjectId is Guid other && other != projectId)
                return await MergeLinkedAsync(other, projectId, sourceId, targetId, userId, workspaceId, host, message, resolve, automatic, cancellationToken);

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

                IReadOnlyList<MergeFileRow> files = await ReadFilesAsync(connection, sourceId, targetId, baseBranch, baseRevision, null, false, cancellationToken);

                MergeOutcome? outcome = await WriteAsync(connection, projectId, sourceId, targetId, userId, workspaceId, host, message, resolve, automatic,
                    new PlanState(pair.SourceHead, pair.TargetHead, pair.LastMergeId), null, files, cancellationToken);
                if (outcome is not null) return outcome;
                if (attempt == Attempts) throw new TimeoutException("The branches kept changing while merging. Try again in a moment.");
            }
        }

        /// <summary>
        /// A merge between a branch and its upstream, in either direction (see the class remarks).
        /// </summary>
        /// <returns><see cref="MergeStatus.Unrelated"/> when neither branch is the other's upstream.</returns>
        private async Task<MergeOutcome> MergeLinkedAsync(Guid sourceProjectId, Guid targetProjectId, Guid sourceId, Guid targetId, Guid userId, Guid? workspaceId,
            string? host, string message, MergeResolve resolve, bool automatic, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                await using NpgsqlConnection connection = _database.CreateConnection();
                await connection.OpenAsync(cancellationToken);

                LinkPair? link = await connection.QuerySingleOrDefaultAsync<LinkPair>(new CommandDefinition(LinkSql,
                    new { SourceProject = sourceProjectId, TargetProject = targetProjectId, Source = sourceId, Target = targetId }, cancellationToken: cancellationToken));
                if (link is null) return new MergeOutcome(MergeStatus.Unrelated);

                IReadOnlyList<MergeFileRow> files;
                if (link.Pull)
                {
                    // The upstream as the downstream last took it in, from the upstream's own history; nothing before
                    // the first merge, so it copies every file the downstream doesn't have.
                    files = await ReadFilesAsync(connection, sourceId, targetId, link.Agreed is null ? Guid.Empty : sourceId, link.Agreed ?? 0, null, false, cancellationToken);
                }
                else
                {
                    // Into the upstream: only from a downstream that has taken in its latest. Then the base is the
                    // upstream's head, and every difference in the paths it owns is the downstream's own change.
                    if ((link.Agreed ?? 0) != link.TargetHead) return new MergeOutcome(MergeStatus.Behind);
                    files = (await ReadFilesAsync(connection, sourceId, targetId, Guid.Empty, 0, link.Paths, link.Paths is null, cancellationToken))
                        .Select(f => f with { BaseFileId = f.TargetFileId }).ToList();
                }

                MergeOutcome? outcome = await WriteAsync(connection, targetProjectId, sourceId, targetId, userId, workspaceId, host, message, resolve, automatic,
                    new PlanState(link.SourceHead, link.TargetHead, link.LastMergeId), new LinkPlan(link.Pull, link.Agreed, sourceProjectId), files, cancellationToken);
                if (outcome is not null) return outcome;
                if (attempt == Attempts) throw new TimeoutException("The branches kept changing while merging. Try again in a moment.");
            }
        }

        /// <summary>
        /// Every path whose content differs between the branches, with its id at the base (<see cref="FilesSql"/>).
        /// </summary>
        /// <param name="baseBranch">The branch whose history holds the base, or <see cref="Guid.Empty"/> for no base.</param>
        /// <param name="paths">Only these prefixes and files, when <paramref name="autoScope"/> or this is set.</param>
        /// <param name="autoScope">Only the target's top-level folders and files (plus <paramref name="paths"/>).</param>
        private static async Task<IReadOnlyList<MergeFileRow>> ReadFilesAsync(NpgsqlConnection connection, Guid sourceId, Guid targetId, Guid baseBranch, long baseRevision,
            string[]? paths, bool autoScope, CancellationToken cancellationToken)
        {
            object parameters = new
            {
                Source = sourceId, Target = targetId, BaseBranch = baseBranch, Base = baseRevision, Paths = paths ?? [], AutoScope = autoScope,
                Scoped = autoScope || paths is not null, ScopeBranch = targetId
            };

            return (await connection.QueryAsync<MergeFileRow>(new CommandDefinition(FilesSql, parameters, commandTimeout: 300, cancellationToken: cancellationToken))).ToReadOnly();
        }

        /// <summary>
        /// Classifies the planned files and writes the merge under the project's counters row.
        /// </summary>
        /// <param name="pair">What the plan read, to detect changes since.</param>
        /// <param name="link">For a merge between a branch and its upstream; null within a project.</param>
        /// <returns>Null when either branch changed since the plan was read.</returns>
        private static async Task<MergeOutcome?> WriteAsync(NpgsqlConnection connection, Guid projectId, Guid sourceId, Guid targetId, Guid userId, Guid? workspaceId,
            string? host, string message, MergeResolve resolve, bool automatic, PlanState pair, LinkPlan? link, IReadOnlyList<MergeFileRow> files,
            CancellationToken cancellationToken)
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
            // Except that a downstream taking in upstream revisions it already matches still records that it's in step.
            bool inStep = link is not { Pull: true } || link.Agreed == pair.SourceHead;
            if (applied.Count == 0 && conflicts.Count == 0 && resolved == 0 && inStep) return new MergeOutcome(MergeStatus.UpToDate);

            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            PlanState? current;
            List<LockRow> locks = new();
            long locked;
            await using (NpgsqlBatch read = new(connection, transaction))
            {
                NpgsqlBatchCommand lockCommand = new(EventStore.LockProjectSql);
                lockCommand.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                read.BatchCommands.Add(lockCommand);

                NpgsqlBatchCommand pairCommand = new(StateSql);
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
                        ? new PlanState(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2))
                        : null;
                    await reader.NextResultAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        locks.Add(new LockRow(reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
                }
            }

            if (current is null) throw new ArgumentException("A branch doesn't exist in this project.");
            if (current != pair) return null;
            if (locks.Count > 0) return new MergeOutcome(MergeStatus.Locked, Locks: locks);

            if (conflicts.Count > 0)
            {
                // Nothing is merged, but people following the target should hear the merge was tried.
                long refusedSeq = await EventStore.AppendAsync(connection, transaction, projectId, new NewEvent(EventTypes.MergeRefused, targetId, userId, workspaceId, host,
                    new MergePayload(sourceId, pair.SourceHead, null, 0, conflicts.Take(EventPaths).Select(c => c.Path).ToArray(), automatic, link?.SourceProjectId)), cancellationToken);
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

                // Between a branch and its upstream, the revisions the merge leaves in step: the downstream has taken in
                // the upstream as of upstream_revision (the base of their next merge) at its own downstream_revision.
                long targetAfter = number ?? pair.TargetHead;
                (long Upstream, long Downstream)? inStepAt = link is null ? null : link.Pull ? (pair.SourceHead, targetAfter) : (targetAfter, pair.SourceHead);
                long createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                NpgsqlBatchCommand record = new("INSERT INTO revisions.merges (project_id, source_branch_id, target_branch_id, source_revision, target_revision, user_id, created_at, " +
                                                "upstream_revision, downstream_revision) " +
                                                "VALUES (@ProjectId, @Source, @Target, @SourceRevision, @TargetRevision, @UserId, @CreatedAt, @UpstreamRevision, @DownstreamRevision);");
                record.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                record.Parameters.Add(new NpgsqlParameter<Guid>("Source", sourceId));
                record.Parameters.Add(new NpgsqlParameter<Guid>("Target", targetId));
                record.Parameters.Add(new NpgsqlParameter<long>("SourceRevision", pair.SourceHead));
                record.Parameters.Add(new NpgsqlParameter("TargetRevision", (object?)number ?? DBNull.Value));
                record.Parameters.Add(new NpgsqlParameter<Guid>("UserId", userId));
                record.Parameters.Add(new NpgsqlParameter<long>("CreatedAt", createdAt));
                record.Parameters.Add(new NpgsqlParameter("UpstreamRevision", (object?)inStepAt?.Upstream ?? DBNull.Value));
                record.Parameters.Add(new NpgsqlParameter("DownstreamRevision", (object?)inStepAt?.Downstream ?? DBNull.Value));
                write.BatchCommands.Add(record);

                if (link is null)
                {
                    NpgsqlBatchCommand carry = new(CarryUpstreamsSql);
                    carry.Parameters.Add(new NpgsqlParameter<Guid>("ProjectId", projectId));
                    carry.Parameters.Add(new NpgsqlParameter<Guid>("Source", sourceId));
                    carry.Parameters.Add(new NpgsqlParameter<Guid>("Target", targetId));
                    carry.Parameters.Add(new NpgsqlParameter<long>("SourceRevision", pair.SourceHead));
                    carry.Parameters.Add(new NpgsqlParameter<long>("Downstream", targetAfter));
                    carry.Parameters.Add(new NpgsqlParameter<Guid>("UserId", userId));
                    carry.Parameters.Add(new NpgsqlParameter<long>("CreatedAt", createdAt));
                    write.BatchCommands.Add(carry);
                }

                write.BatchCommands.Add(EventStore.CreateAppendCommand(projectId, new NewEvent(EventTypes.MergeCompleted, targetId, userId, workspaceId, host,
                    new MergePayload(sourceId, pair.SourceHead, number, applied.Count, [], automatic, link?.SourceProjectId))));

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

        /// <summary>What a plan depends on: both heads and the latest merge between the two (<see cref="StateSql"/>).</summary>
        private sealed record PlanState(long SourceHead, long TargetHead, long? LastMergeId);

        /// <summary>A branch and its upstream (<see cref="LinkSql"/>).</summary>
        /// <param name="Pull">The target is the downstream: the merge takes in upstream revisions.</param>
        /// <param name="Paths">What the upstream owns, or null for its top-level folders and files.</param>
        /// <param name="Agreed">The upstream revision the downstream last took in, or null before the first merge.</param>
        /// <remarks>A class with setters because Dapper can't pass a text[] column to a constructor.</remarks>
        private sealed class LinkPair
        {
            public bool Pull { get; set; }
            public string[]? Paths { get; set; }
            public long SourceHead { get; set; }
            public long TargetHead { get; set; }
            public long? LastMergeId { get; set; }
            public long? Agreed { get; set; }
        }

        /// <param name="SourceProjectId">The source's project, for the events.</param>
        private sealed record LinkPlan(bool Pull, long? Agreed, Guid SourceProjectId);

        private sealed record MergeFileRow(string PathKey, string Path, string? BaseFileId, string? SourceFileId, string? TargetFileId);
    }
}
