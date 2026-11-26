// Copyright 2026 Entex Interactive

using Dapper;
using Npgsql;
using Vertex.Core.Data;
using Vertex.Server.Database;
using Vertex.Server.History;

namespace Vertex.Server.Branches
{
    /// <summary>
    /// Branches' upstreams (<c>branches.upstreams</c>): a branch of another project a branch takes its files from, such
    /// as a studio engine a game is built on. The files are merged both ways by <see cref="MergesRepository"/>; this is
    /// the link itself and how far apart the two are.
    /// </summary>
    /// <remarks>
    /// A branch has at most one upstream for now (the table allows more, e.g. an engine plus a shared plugin pack).
    /// Changing it takes the downstream project's counters row, so it never races a merge into the branch or a branch
    /// being made from it (which inherits the link).
    /// </remarks>
    public class UpstreamsRepository
    {
        /// <summary>The longest chain of upstreams the loop check follows.</summary>
        private const int MaximumDepth = 64;

        private const string LinkColumns =
            "u.branch_id AS BranchId, ub.project_id AS UpstreamProjectId, p.name AS UpstreamProject, ub.id AS UpstreamBranchId, ub.name AS UpstreamBranch, " +
            "u.paths AS Paths, u.read_only AS ReadOnly";

        private const string LinksSql =
            $"SELECT {LinkColumns} FROM branches.upstreams u " +
            "JOIN branches.branches b ON b.id = u.branch_id AND b.project_id = @ProjectId " +
            "JOIN branches.branches ub ON ub.id = u.upstream_id JOIN projects.projects p ON p.id = ub.project_id " +
            "WHERE u.branch_id = @BranchId ORDER BY p.name, ub.name;";

        /// <summary>
        /// How far a branch and its upstream are apart: the upstream revision the branch took in last, the upstream's
        /// head, the upstream revisions after it, and (with @Differences) how many files the upstream owns differ
        /// between the two heads.
        /// </summary>
        private static readonly string StatusSql =
            "WITH RECURSIVE taken AS (" +
            "  SELECT MAX(x.upstream_revision) AS revision FROM revisions.merges x " +
            "  WHERE (x.source_branch_id = @Upstream AND x.target_branch_id = @BranchId) OR (x.source_branch_id = @BranchId AND x.target_branch_id = @Upstream)" +
            "), scope AS (" + MergesRepository.ScopeSql + ") " +
            "SELECT (SELECT revision FROM taken) AS Taken, " +
            "GREATEST(ub.base_revision, COALESCE((SELECT MAX(r.number) FROM revisions.revisions r WHERE r.branch_id = ub.id), 0)) AS Head, " +
            "(SELECT COUNT(*) FROM revisions.revisions r WHERE r.branch_id = ub.id AND r.number > COALESCE((SELECT revision FROM taken), 0))::INT AS Behind, " +
            "CASE WHEN @Differences THEN (SELECT COUNT(*) FROM (SELECT path_key, file_id FROM revisions.head WHERE branch_id = @BranchId) s " +
            "  FULL JOIN (SELECT path_key, file_id FROM revisions.head WHERE branch_id = @Upstream) t ON t.path_key = s.path_key " +
            "  WHERE s.file_id IS DISTINCT FROM t.file_id AND " + MergesRepository.InScope("COALESCE(s.path_key, t.path_key)") + ")::INT ELSE 0 END AS Differing " +
            "FROM branches.branches ub WHERE ub.id = @Upstream;";

        private readonly PostgresDatabase _database;

        public UpstreamsRepository(PostgresDatabase database)
        {
            _database = database;
        }

        /// <summary>
        /// A branch's upstreams; with <paramref name="status"/>, also how far behind it is (cheap), and with
        /// <paramref name="differences"/> how many owned files differ (compares both branches' file lists).
        /// </summary>
        public async Task<IReadOnlyList<VUpstream>> ListAsync(Guid projectId, Guid branchId, bool status, bool differences, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            List<VUpstream> links = (await connection.QueryAsync<LinkRow>(new CommandDefinition(LinksSql, new { ProjectId = projectId, BranchId = branchId },
                cancellationToken: cancellationToken))).Select(row => row.ToUpstream()).ToList();
            if (!status) return links;

            for (int i = 0; i < links.Count; i++)
            {
                VUpstream link = links[i];
                StatusRow row = await connection.QuerySingleAsync<StatusRow>(new CommandDefinition(StatusSql, new
                {
                    BranchId = branchId, Upstream = link.UpstreamBranchId, Differences = differences, Paths = link.Paths ?? [], AutoScope = link.Paths is null,
                    ScopeBranch = link.UpstreamBranchId
                }, commandTimeout: 120, cancellationToken: cancellationToken));
                links[i] = link with { TakenRevision = row.Taken, UpstreamHead = row.Head, Behind = row.Behind, Differing = row.Differing };
            }

            return links;
        }

        /// <summary>
        /// Makes <paramref name="upstreamId"/> the branch's upstream (replacing any other), or changes its settings.
        /// </summary>
        /// <param name="paths">What the upstream owns; null keeps what the link has (the upstream's top-level entries for a
        /// new one), and an empty array resets it to those.</param>
        /// <param name="readOnly">Refuse submits to the upstream's paths; null keeps the link's setting (off for a new one).
        /// A read-only link without paths gets the upstream's current top-level entries, so submits check a fixed list.</param>
        /// <returns>The link, or null when the branch doesn't exist in the project.</returns>
        /// <exception cref="ArgumentException">The upstream doesn't exist, is in the same project, or would make a loop.</exception>
        public async Task<VUpstream?> SetAsync(Guid projectId, Guid branchId, Guid upstreamId, string[]? paths, bool? readOnly, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("SELECT 1 FROM projects.counters WHERE project_id = @ProjectId FOR UPDATE;", new { ProjectId = projectId },
                transaction, cancellationToken: cancellationToken));

            const string branchesSql = "SELECT b.name AS Name, ub.project_id AS UpstreamProject FROM branches.branches b, branches.branches ub " +
                                       "WHERE b.id = @BranchId AND b.project_id = @ProjectId AND ub.id = @Upstream;";
            BranchesRow? found = await connection.QuerySingleOrDefaultAsync<BranchesRow>(new CommandDefinition(branchesSql,
                new { BranchId = branchId, ProjectId = projectId, Upstream = upstreamId }, transaction, cancellationToken: cancellationToken));
            if (found is null)
            {
                bool branchExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM branches.branches WHERE id = @BranchId AND project_id = @ProjectId);", new { BranchId = branchId, ProjectId = projectId },
                    transaction, cancellationToken: cancellationToken));
                if (!branchExists) return null;
                throw new ArgumentException("The upstream branch doesn't exist.");
            }

            if (found.UpstreamProject == projectId)
                throw new ArgumentException("That branch is in the same project. Branches of one project already share history: merge them with 'vx merge'.");

            // Following the upstream's own upstreams must never come back to this branch.
            const string loopSql = "WITH RECURSIVE c AS (" +
                                   "  SELECT upstream_id, 0 AS depth FROM branches.upstreams WHERE branch_id = @Upstream " +
                                   "  UNION SELECT u.upstream_id, c.depth + 1 FROM c JOIN branches.upstreams u ON u.branch_id = c.upstream_id WHERE c.depth < @Depth" +
                                   ") SELECT EXISTS (SELECT 1 FROM c WHERE upstream_id = @BranchId);";
            if (upstreamId == branchId || await connection.ExecuteScalarAsync<bool>(new CommandDefinition(loopSql, new { Upstream = upstreamId, BranchId = branchId, Depth = MaximumDepth },
                    transaction, cancellationToken: cancellationToken)))
                throw new ArgumentException($"That branch already takes its files from '{found.Name}' (directly or through others), so this would be a loop.");

            // One upstream per branch: a new one replaces the old link. Merges with the old one stay in history.
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM branches.upstreams WHERE branch_id = @BranchId AND upstream_id <> @Upstream;",
                new { BranchId = branchId, Upstream = upstreamId }, transaction, cancellationToken: cancellationToken));

            string[]? normalized = paths is null ? null : Normalize(paths);
            const string upsertSql =
                "INSERT INTO branches.upstreams (branch_id, upstream_id, paths, read_only, created_at) " +
                "VALUES (@BranchId, @Upstream, @Paths::TEXT[], COALESCE(@ReadOnly::BOOLEAN, FALSE), @CreatedAt) " +
                "ON CONFLICT (branch_id, upstream_id) DO UPDATE SET " +
                "paths = CASE WHEN @SetPaths THEN excluded.paths ELSE branches.upstreams.paths END, " +
                "read_only = COALESCE(@ReadOnly::BOOLEAN, branches.upstreams.read_only);";
            await connection.ExecuteAsync(new CommandDefinition(upsertSql, new
            {
                BranchId = branchId, Upstream = upstreamId, Paths = normalized is { Length: > 0 } ? normalized : null, SetPaths = normalized is not null, ReadOnly = readOnly,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, transaction, cancellationToken: cancellationToken));

            // A read-only link checks every submit against its paths, so it keeps a fixed list rather than reading the
            // upstream's file list each time.
            const string fixSql = "UPDATE branches.upstreams SET paths = (SELECT COALESCE(array_agg(DISTINCT split_part(h.path, '/', 1) ORDER BY split_part(h.path, '/', 1)), '{}') " +
                                  "FROM revisions.head h WHERE h.branch_id = @Upstream) WHERE branch_id = @BranchId AND upstream_id = @Upstream AND read_only AND paths IS NULL;";
            await connection.ExecuteAsync(new CommandDefinition(fixSql, new { BranchId = branchId, Upstream = upstreamId }, transaction, commandTimeout: 120,
                cancellationToken: cancellationToken));

            LinkRow row = await connection.QuerySingleAsync<LinkRow>(new CommandDefinition(
                $"SELECT {LinkColumns} FROM branches.upstreams u JOIN branches.branches ub ON ub.id = u.upstream_id JOIN projects.projects p ON p.id = ub.project_id " +
                "WHERE u.branch_id = @BranchId AND u.upstream_id = @Upstream;", new { BranchId = branchId, Upstream = upstreamId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return row.ToUpstream();
        }

        /// <summary>
        /// Removes a branch's upstreams. The files and the merge history stay.
        /// </summary>
        /// <returns>The links removed; empty when the branch had none (or doesn't exist).</returns>
        public async Task<IReadOnlyList<VUpstream>> ClearAsync(Guid projectId, Guid branchId, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = _database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("SELECT 1 FROM projects.counters WHERE project_id = @ProjectId FOR UPDATE;", new { ProjectId = projectId },
                transaction, cancellationToken: cancellationToken));

            List<VUpstream> links = (await connection.QueryAsync<LinkRow>(new CommandDefinition(LinksSql, new { ProjectId = projectId, BranchId = branchId }, transaction,
                cancellationToken: cancellationToken))).Select(row => row.ToUpstream()).ToList();
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM branches.upstreams WHERE branch_id = @BranchId;", new { BranchId = branchId }, transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return links;
        }

        /// <summary>
        /// "Engine", "Engine/", "\Engine\Source\" → "Engine", "Engine/Source": no outer slashes, '/' separators, no
        /// duplicates. Empty entries are dropped.
        /// </summary>
        /// <exception cref="ArgumentException">An entry isn't a valid relative path.</exception>
        public static string[] Normalize(IEnumerable<string> paths)
        {
            List<string> result = new();
            foreach (string raw in paths)
            {
                string path = raw.Replace('\\', '/').Trim().Trim('/');
                if (path.Length == 0) continue;
                if (path.Split('/').Any(part => part is "" or "." or ".."))
                    throw new ArgumentException($"'{raw}' isn't a folder or file path inside the project.");
                if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
            }

            return result.ToArray();
        }

        /// <summary>A link as read; a class with setters because Dapper can't pass a text[] column to a constructor.</summary>
        private sealed class LinkRow
        {
            public Guid BranchId { get; set; }
            public Guid UpstreamProjectId { get; set; }
            public string UpstreamProject { get; set; } = string.Empty;
            public Guid UpstreamBranchId { get; set; }
            public string UpstreamBranch { get; set; } = string.Empty;
            public string[]? Paths { get; set; }
            public bool ReadOnly { get; set; }

            public VUpstream ToUpstream()
            {
                return new VUpstream(BranchId, UpstreamProjectId, UpstreamProject, UpstreamBranchId, UpstreamBranch, Paths, ReadOnly);
            }
        }

        private sealed record StatusRow(long? Taken, long Head, int Behind, int Differing);

        private sealed record BranchesRow(string Name, Guid UpstreamProject);
    }
}
