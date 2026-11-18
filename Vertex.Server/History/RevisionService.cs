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
using Vertex.Server.FileServices;

namespace Vertex.Server.History
{
    /// <summary>
    /// gRPC endpoint for project history: submitting revisions and reading the branch head. See Files.proto.
    /// </summary>
    public class RevisionService : Revisions.RevisionsBase
    {
        /// <summary>Outdated paths named in a refused submit before "and N more".</summary>
        private const int ListedPaths = 10;

        /// <summary>Revisions GetRevisions returns when the request names no limit, and the most it ever returns.</summary>
        private const int DefaultRevisionsPerPage = 100;
        private const int MaximumRevisionsPerPage = 1_000;

        private readonly INotificationService _notifications;
        private readonly ILogger<RevisionService> _logger;
        private readonly RevisionsRepository _revisions;
        private readonly AccessService _access;
        private readonly FilesRepository _files;
        private readonly CacheContext _cache;

        public RevisionService(ILogger<RevisionService> logger, RevisionsRepository revisions, AccessService access, FilesRepository files, CacheContext cache, INotificationService notifications)
        {
            _notifications = notifications;
            _cache = cache;
            _logger = logger;
            _revisions = revisions;
            _access = access;
            _files = files;
        }

        public override async Task<SubmitResponse> Submit(SubmitRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            ClaimsPrincipal user = context.GetHttpContext().User;
            Guid projectId = ParseId(request.ProjectId, "project");
            Guid branchId = ParseId(request.BranchId, "branch");
            Guid workspaceId = ParseId(request.WorkspaceId, "workspace");

            string message = request.Message.Trim();
            if (message.Length == 0) throw Invalid("Describe the revision: vx submit -m \"What changed\".");
            if (request.Changes.Count == 0) throw Invalid("The submit has no changes.");
            if (request.Changes.Count > FileTransferLimits.MaximumChangesPerSubmit)
                throw Invalid($"A submit can have at most {FileTransferLimits.MaximumChangesPerSubmit:N0} files. Submit a folder at a time: vx submit -m \"...\" <folder>.");

            List<SubmitChange> changes = new(request.Changes.Count);
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (FileChange change in request.Changes)
            {
                if (!RepositoryPath.IsValid(change.Path)) throw Invalid($"'{change.Path}' is not a valid file path.");
                string key = RepositoryPath.ToKey(change.Path);
                if (!keys.Add(key)) throw Invalid($"'{change.Path}' is listed twice.");

                changes.Add(new SubmitChange(change.Path, key, ParseFileId(change.FileId), ParseFileId(change.BaseFileId)));
            }

            await RequireAsync(user, projectId, branchId, AccessLevel.Write, ct);

            // A revision may only point at content the server stores.
            string[] fileIds = changes.Where(c => c.FileId is not null).Select(c => c.FileId!).Distinct(StringComparer.Ordinal).ToArray();
            HashSet<string> stored = await _files.GetStoredManifestsAsync(fileIds, ct);
            string? missing = fileIds.FirstOrDefault(id => !stored.Contains(id));
            if (missing is not null)
            {
                string path = changes.First(c => c.FileId == missing).Path;
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The content of '{path}' was not uploaded. Run 'vx submit' again."));
            }

            string host = request.Host.Trim() is { Length: > 0 } given ? given[..Math.Min(given.Length, 255)] : "unknown host";
            SubmitOutcome outcome = await _revisions.SubmitAsync(projectId, branchId, user.GetUserId(), workspaceId, host, message, changes, ct);
            if (outcome.OutOfDate.Count > 0)
            {
                VertexMetrics.SubmitsRefused.Add(1, new KeyValuePair<string, object?>("reason", "out_of_date"));
                string listed = string.Join(", ", outcome.OutOfDate.Take(ListedPaths));
                string more = outcome.OutOfDate.Count > ListedPaths ? $" and {outcome.OutOfDate.Count - ListedPaths:N0} more" : "";
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"Changed on the server since your last sync: {listed}{more}. Run 'vx sync', then submit again."));
            }

            if (outcome.LockedByOthers.Count > 0) VertexMetrics.SubmitsRefused.Add(1, new KeyValuePair<string, object?>("reason", "locked"));
            if (outcome.LockedByOthers.Count > 0)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, ProjectCalls.DescribeLockedByOthers(outcome.LockedByOthers, user.GetUserId())));

            if (outcome.Revision is long revision) await _revisions.CacheHeadRevisionAsync(branchId, revision);
            await ProjectCalls.AfterCommitAsync(_cache, _notifications, projectId, outcome.EventSeq);
            if (outcome.Revision is null) throw new RpcException(new Status(StatusCode.FailedPrecondition, "Nothing to submit: the server already has these changes."));

            VertexMetrics.Revisions.Add(1);
            VertexMetrics.RevisionFiles.Record(changes.Count);
            VertexMetrics.LocksReleased.Add(outcome.LocksReleased, new KeyValuePair<string, object?>("reason", "submitted"));
            VertexMetrics.EventsRecorded.Add(1, VertexMetrics.Tag("type", EventTypes.RevisionCreated));
            if (outcome.LocksReleased > 0) VertexMetrics.EventsRecorded.Add(1, VertexMetrics.Tag("type", EventTypes.LockReleased));
            _logger.LogInformation("Revision #{Revision} in project {ProjectId}: {Files} file(s) by {User}", outcome.Revision, projectId, changes.Count, user.GetUsername());
            return new SubmitResponse { Revision = outcome.Revision.Value };
        }

        /// <summary>
        /// Sync. Pages through the files that differ between the workspace's synced revision and the target (the
        /// latest by default), or, when that can't be read from the branch head, every file at the target.
        /// </summary>
        /// <remarks>
        /// Syncing to the latest from a revision at or after the branch's base reads <c>revisions.head</c> (the fast
        /// path every normal sync takes). An older target, or a workspace synced to before the branch was copied from
        /// its parent, rebuilds the full file list from history (<see cref="RevisionsRepository.GetFilesAtRevisionAsync"/>)
        /// and sets <c>full</c>, so the client also removes tracked files that aren't listed.
        /// The page token is <c>target:latest:mode:cursor</c>, so every page reads the same target the same way even
        /// when a submit moves the head between pages.
        /// </remarks>
        public override async Task<GetHeadChangesResponse> GetHeadChanges(GetHeadChangesRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            Guid projectId = ParseId(request.ProjectId, "project");
            Guid branchId = ParseId(request.BranchId, "branch");
            if (request.AfterRevision < 0) throw Invalid("after_revision can't be negative.");
            if (request.Revision < 0) throw Invalid("revision can't be negative.");

            await RequireAsync(context.GetHttpContext().User, projectId, branchId, AccessLevel.Read, ct);

            long target, latest;
            bool fromHistory;
            string cursor;
            if (request.PageToken.Length == 0)
            {
                latest = await _revisions.GetHeadRevisionAsync(branchId, ct);
                target = request.Revision == 0 ? latest : request.Revision;
                if (target > latest)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Revision #{target} is newer than this branch's latest, #{latest}."));

                GetHeadChangesResponse nothing = new() { HeadRevision = target, LatestRevision = latest };
                if (request.AfterRevision == target) return nothing;
                if (target == latest)
                {
                    if (request.AfterRevision > latest) return nothing;
                    fromHistory = request.AfterRevision > 0 && request.AfterRevision < await _revisions.GetBaseRevisionAsync(branchId, ct);
                }
                else
                {
                    fromHistory = true;
                }

                cursor = string.Empty;
            }
            else
            {
                string[] parts = request.PageToken.Split(':', 4);
                if (parts.Length != 4 || !long.TryParse(parts[0], out target) || !long.TryParse(parts[1], out latest) || parts[2] is not ("h" or "f"))
                    throw Invalid("Invalid page token.");

                fromHistory = parts[2] == "f";
                cursor = parts[3];
            }

            GetHeadChangesResponse response = new() { HeadRevision = target, LatestRevision = latest, Full = fromHistory || request.AfterRevision == 0 };
            IReadOnlyList<HeadFileRow> rows = fromHistory
                ? await _revisions.GetFilesAtRevisionAsync(branchId, target, cursor, FileTransferLimits.HeadChangesPageSize, ct)
                : await _revisions.GetHeadChangesAsync(branchId, request.AfterRevision, target, cursor, FileTransferLimits.HeadChangesPageSize, ct);

            response.Files.AddRange(rows.Select(row => new HeadFile { Path = row.Path, FileId = row.FileId ?? string.Empty, Revision = row.Revision }));
            VertexMetrics.SyncFiles.Add(rows.Count);
            if (rows.Count == FileTransferLimits.HeadChangesPageSize) response.NextPageToken = $"{target}:{latest}:{(fromHistory ? "f" : "h")}:{rows[^1].PathKey}";
            return response;
        }

        public override async Task<GetRevisionsResponse> GetRevisions(GetRevisionsRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            Guid projectId = ParseId(request.ProjectId, "project");
            Guid branchId = ParseId(request.BranchId, "branch");
            if (request.AfterRevision < 0) throw Invalid("after_revision can't be negative.");
            if (request.Limit < 0) throw Invalid("limit can't be negative.");
            int limit = request.Limit == 0 ? DefaultRevisionsPerPage : Math.Min(request.Limit, MaximumRevisionsPerPage);

            await RequireAsync(context.GetHttpContext().User, projectId, branchId, AccessLevel.Read, ct);

            IReadOnlyList<RevisionRow> rows = await _revisions.GetRevisionsAsync(branchId, request.AfterRevision, limit, ct);
            GetRevisionsResponse response = new();
            response.Revisions.AddRange(rows.Select(row => new RevisionInfo
            {
                Number = row.Number,
                Message = row.Message,
                Username = row.Username,
                DisplayName = row.DisplayName ?? string.Empty,
                CreatedAt = row.CreatedAt,
                Files = row.Files
            }));
            return response;
        }

        public override async Task<GetRevisionChangesResponse> GetRevisionChanges(GetRevisionChangesRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            Guid projectId = ParseId(request.ProjectId, "project");
            Guid branchId = ParseId(request.BranchId, "branch");
            if (request.Revision < 1) throw Invalid("revision must be at least 1.");

            await RequireAsync(context.GetHttpContext().User, projectId, branchId, AccessLevel.Read, ct);

            // The page token is the last path of the previous page.
            IReadOnlyList<RevisionChangeRow> rows = await _revisions.GetRevisionChangesAsync(projectId, branchId, request.Revision, request.PageToken, FileTransferLimits.HeadChangesPageSize, ct);
            if (rows.Count == 0 && request.PageToken.Length == 0)
                throw new RpcException(new Status(StatusCode.NotFound, $"Revision #{request.Revision} isn't on this branch."));

            GetRevisionChangesResponse response = new();
            response.Changes.AddRange(rows.Select(row => new RevisionFileChange { Path = row.Path, FileId = row.FileId ?? string.Empty }));
            if (rows.Count == FileTransferLimits.HeadChangesPageSize) response.NextPageToken = rows[^1].Path;
            return response;
        }

        private Task RequireAsync(ClaimsPrincipal user, Guid projectId, Guid branchId, AccessLevel level, CancellationToken ct)
        {
            return ProjectCalls.RequireAsync(_access, _revisions, user, projectId, branchId, level, ct);
        }

        private static Guid ParseId(string value, string what)
        {
            return ProjectCalls.ParseId(value, what);
        }

        private static string? ParseFileId(string value)
        {
            if (value.Length == 0) return null;
            return ContentId.IsValidFileId(value) ? value.ToLowerInvariant() : throw Invalid("Invalid file id.");
        }

        private static RpcException Invalid(string message)
        {
            return new RpcException(new Status(StatusCode.InvalidArgument, message));
        }
    }
}
