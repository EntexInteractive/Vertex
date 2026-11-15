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

        private readonly ILogger<RevisionService> _logger;
        private readonly RevisionsRepository _revisions;
        private readonly AccessService _access;
        private readonly FilesRepository _files;
        private readonly CacheContext _cache;
        private readonly INotificationService _notifications;

        public RevisionService(ILogger<RevisionService> logger, RevisionsRepository revisions, AccessService access, FilesRepository files, CacheContext cache,
            INotificationService notifications)
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

        public override async Task<GetHeadChangesResponse> GetHeadChanges(GetHeadChangesRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            Guid projectId = ParseId(request.ProjectId, "project");
            Guid branchId = ParseId(request.BranchId, "branch");
            if (request.AfterRevision < 0) throw Invalid("after_revision can't be negative.");

            await RequireAsync(context.GetHttpContext().User, projectId, branchId, AccessLevel.Read, ct);

            // The token pins the head revision of the first page, so every page reads the same snapshot.
            long head;
            string cursor;
            if (request.PageToken.Length == 0)
            {
                head = await _revisions.GetHeadRevisionAsync(branchId, ct);
                cursor = string.Empty;
            }
            else
            {
                int colon = request.PageToken.IndexOf(':');
                if (colon <= 0 || !long.TryParse(request.PageToken.AsSpan(0, colon), out head)) throw Invalid("Invalid page token.");
                cursor = request.PageToken[(colon + 1)..];
            }

            GetHeadChangesResponse response = new() { HeadRevision = head };
            if (head <= request.AfterRevision) return response;

            IReadOnlyList<HeadFileRow> rows = await _revisions.GetHeadChangesAsync(branchId, request.AfterRevision, head, cursor, FileTransferLimits.HeadChangesPageSize, ct);
            response.Files.AddRange(rows.Select(row => new HeadFile { Path = row.Path, FileId = row.FileId ?? string.Empty, Revision = row.Revision }));
            VertexMetrics.SyncFiles.Add(rows.Count);
            if (rows.Count == FileTransferLimits.HeadChangesPageSize) response.NextPageToken = $"{head}:{rows[^1].PathKey}";
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
