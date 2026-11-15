// Copyright 2026 Entex Interactive

using System.Security.Claims;
using Grpc.Core;
using Vertex.Core.Net;
using Vertex.Core.Security;
using Vertex.Core.Storage;
using Vertex.Server.Access;
using Vertex.Server.Auth;
using Vertex.Server.Database;
using Vertex.Server.Events;
using Vertex.Server.Telemetry;

namespace Vertex.Server.History
{
    /// <summary>
    /// gRPC endpoint for file locks: locking changed files, releasing them, and the event feed clients use to make
    /// files others hold read-only. See Files.proto.
    /// </summary>
    public class LockService : Locks.LocksBase
    {
        private const int MaximumHostLength = 255;

        private readonly ILogger<LockService> _logger;
        private readonly LocksRepository _locks;
        private readonly RevisionsRepository _revisions;
        private readonly AccessService _access;
        private readonly CacheContext _cache;
        private readonly INotificationService _notifications;

        public LockService(ILogger<LockService> logger, LocksRepository locks, RevisionsRepository revisions, AccessService access, CacheContext cache,
            INotificationService notifications)
        {
            _notifications = notifications;
            _logger = logger;
            _locks = locks;
            _revisions = revisions;
            _access = access;
            _cache = cache;
        }

        public override async Task<LockResponse> Lock(LockRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            ClaimsPrincipal user = context.GetHttpContext().User;
            Guid projectId = ProjectCalls.ParseId(request.ProjectId, "project");
            Guid branchId = ProjectCalls.ParseId(request.BranchId, "branch");
            Guid workspaceId = ProjectCalls.ParseId(request.WorkspaceId, "workspace");
            string host = ParseHost(request.Host);
            List<(string Key, string Path)> paths = ParsePaths(request.Paths);

            await ProjectCalls.RequireAsync(_access, _revisions, user, projectId, branchId, AccessLevel.Write, ct);
            Guid userId = user.GetUserId();
            LockOutcome outcome = await _locks.LockAsync(projectId, branchId, userId, workspaceId, host, paths, ct);
            VertexMetrics.LocksGranted.Add(outcome.Granted.Count);
            await ProjectCalls.AfterCommitAsync(_cache, _notifications, projectId, outcome.Seq);

            LockResponse response = new();
            foreach (string path in request.Paths)
            {
                string key = RepositoryPath.ToKey(path);
                if (outcome.Granted.Contains(key))
                {
                    response.Results.Add(new LockResult { Path = path, Status = LockStatus.Granted });
                }
                else if (outcome.Existing.TryGetValue(key, out LockRow? holder))
                {
                    bool mine = holder.WorkspaceId == workspaceId;
                    response.Results.Add(new LockResult
                    {
                        Path = path,
                        Status = mine ? LockStatus.AlreadyYours : LockStatus.Held,
                        Username = holder.Username,
                        Host = holder.Host,
                        SameUser = holder.UserId == userId
                    });
                }
                else
                {
                    // Listed twice in the request: the first occurrence took it.
                    response.Results.Add(new LockResult { Path = path, Status = LockStatus.AlreadyYours });
                }
            }

            if (outcome.Granted.Count > 0) _logger.LogDebug("{User} locked {Count} file(s) in project {ProjectId}", user.GetUsername(), outcome.Granted.Count, projectId);
            return response;
        }

        public override async Task<UnlockResponse> Unlock(UnlockRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            ClaimsPrincipal user = context.GetHttpContext().User;
            Guid projectId = ProjectCalls.ParseId(request.ProjectId, "project");
            Guid branchId = ProjectCalls.ParseId(request.BranchId, "branch");
            Guid workspaceId = ProjectCalls.ParseId(request.WorkspaceId, "workspace");
            List<(string Key, string Path)> paths = ParsePaths(request.Paths);

            await ProjectCalls.RequireAsync(_access, _revisions, user, projectId, branchId, AccessLevel.Write, ct);

            // Forcing other people's locks open is a project admin's call; your own stale locks are always yours.
            bool isAdmin = request.Force && (await _access.CheckAsync(user, projectId, AccessLevel.Admin, ct)).Allowed;
            (IReadOnlyList<LockRow> released, long? seq) = await _locks.UnlockAsync(projectId, branchId, user.GetUserId(), workspaceId,
                paths.Select(p => p.Key).ToArray(), request.Force, isAdmin, ct);

            VertexMetrics.LocksReleased.Add(released.Count, new KeyValuePair<string, object?>("reason", request.Force ? "unlocked" : "reverted"));
            await ProjectCalls.AfterCommitAsync(_cache, _notifications, projectId, seq);
            foreach (LockRow lockRow in released.Where(l => l.UserId != user.GetUserId()))
                _logger.LogInformation("{User} force-released {Owner}'s lock on {Path} in project {ProjectId}", user.GetUsername(), lockRow.Username, lockRow.Path, projectId);

            UnlockResponse response = new();
            response.Released.AddRange(released.Select(l => l.Path));
            return response;
        }

        public override async Task<GetLockEventsResponse> GetLockEvents(GetLockEventsRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            Guid projectId = ProjectCalls.ParseId(request.ProjectId, "project");
            Guid branchId = ProjectCalls.ParseId(request.BranchId, "branch");

            await ProjectCalls.RequireAsync(_access, _revisions, context.GetHttpContext().User, projectId, branchId, AccessLevel.Read, ct);

            // Nothing changed since the client's cursor: answer from the shared cache without touching PostgreSQL.
            // This is the call every idle client makes, so it has to stay this cheap.
            if (request.AfterSeq > 0 && await _cache.GetInt64Async(ProjectCalls.EventSeqKey(projectId)) == request.AfterSeq)
                return new GetLockEventsResponse { LatestSeq = request.AfterSeq };

            LockChanges changes = await _locks.GetChangesAsync(projectId, branchId, request.AfterSeq, ct);
            await ProjectCalls.CacheEventSeqAsync(_cache, projectId, changes.LatestSeq);

            GetLockEventsResponse response = new() { LatestSeq = changes.LatestSeq, Reset = changes.Reset };
            response.Events.AddRange(changes.Events.Select(e => new LockEvent
            {
                Seq = e.Seq,
                Released = e.Released,
                Lock = new LockInfo { Path = e.Path, Username = e.Username, WorkspaceId = e.WorkspaceId?.ToString() ?? string.Empty, Host = e.Host ?? string.Empty, CreatedAt = e.CreatedAt }
            }));

            response.Locks.AddRange(changes.Locks.Select(l => new LockInfo
            {
                Path = l.Path, Username = l.Username, WorkspaceId = l.WorkspaceId.ToString(), Host = l.Host, CreatedAt = l.CreatedAt
            }));

            return response;
        }

        private static List<(string Key, string Path)> ParsePaths(IReadOnlyCollection<string> paths)
        {
            if (paths.Count == 0) throw ProjectCalls.Invalid("No paths given.");
            if (paths.Count > FileTransferLimits.MaximumPathsPerLockRequest)
                throw ProjectCalls.Invalid($"At most {FileTransferLimits.MaximumPathsPerLockRequest:N0} paths per request.");

            List<(string Key, string Path)> parsed = new(paths.Count);
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                if (!RepositoryPath.IsValid(path)) throw ProjectCalls.Invalid($"'{path}' is not a valid file path.");
                string key = RepositoryPath.ToKey(path);
                if (seen.Add(key)) parsed.Add((key, path));
            }

            return parsed;
        }

        private static string ParseHost(string host)
        {
            host = host.Trim();
            if (host.Length == 0) return "unknown host";
            return host.Length > MaximumHostLength ? host[..MaximumHostLength] : host;
        }
    }
}
