// Copyright 2026 Entex Interactive

using System.Security.Claims;
using Grpc.Core;
using Vertex.Core.Net;
using Vertex.Core.Security;
using Vertex.Core.Storage;
using Vertex.Server.Access;
using Vertex.Server.Auth;
using Vertex.Server.Storage;

namespace Vertex.Server.History
{
    /// <summary>
    /// gRPC endpoint for project history: submitting revisions and reading the branch head. See Files.proto.
    /// </summary>
    public class RevisionService : Revisions.RevisionsBase
    {
        /// <summary>Concurrent S3 existence checks per submit.</summary>
        private const int LookupConcurrency = 32;

        /// <summary>Outdated paths named in a refused submit before "and N more".</summary>
        private const int ListedPaths = 10;

        private readonly ILogger<RevisionService> _logger;
        private readonly RevisionsRepository _revisions;
        private readonly AccessService _access;
        private readonly IContentStore _store;

        public RevisionService(ILogger<RevisionService> logger, RevisionsRepository revisions, AccessService access, IContentStore store)
        {
            _logger = logger;
            _revisions = revisions;
            _access = access;
            _store = store;
        }

        public override async Task<SubmitResponse> Submit(SubmitRequest request, ServerCallContext context)
        {
            CancellationToken ct = context.CancellationToken;
            ClaimsPrincipal user = context.GetHttpContext().User;
            Guid projectId = ParseId(request.ProjectId, "project");
            Guid branchId = ParseId(request.BranchId, "branch");

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
            bool[] exists = new bool[fileIds.Length];
            await Parallel.ForAsync(0, fileIds.Length, new ParallelOptions { MaxDegreeOfParallelism = LookupConcurrency, CancellationToken = ct },
                async (i, token) => exists[i] = await _store.ManifestExistsAsync(fileIds[i], token));

            int missing = Array.IndexOf(exists, false);
            if (missing >= 0)
            {
                string path = changes.First(c => c.FileId == fileIds[missing]).Path;
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The content of '{path}' was not uploaded. Run 'vx submit' again."));
            }

            SubmitOutcome outcome = await _revisions.SubmitAsync(projectId, branchId, user.GetUserId(), message, changes, ct);
            if (outcome.OutOfDate.Count > 0)
            {
                string listed = string.Join(", ", outcome.OutOfDate.Take(ListedPaths));
                string more = outcome.OutOfDate.Count > ListedPaths ? $" and {outcome.OutOfDate.Count - ListedPaths:N0} more" : "";
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"Changed on the server since your last sync: {listed}{more}. Run 'vx sync', then submit again."));
            }

            if (outcome.Revision is null) throw new RpcException(new Status(StatusCode.FailedPrecondition, "Nothing to submit: the server already has these changes."));

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
            if (rows.Count == FileTransferLimits.HeadChangesPageSize) response.NextPageToken = $"{head}:{rows[^1].PathKey}";
            return response;
        }

        /// <summary>
        /// Throws unless the caller has <paramref name="level"/> in the project and the branch belongs to it.
        /// </summary>
        private async Task RequireAsync(ClaimsPrincipal user, Guid projectId, Guid branchId, AccessLevel level, CancellationToken ct)
        {
            AccessCheck check = await _access.CheckAsync(user, projectId, level, ct);
            if (!check.Allowed)
            {
                StatusCode code = check.StatusCode == StatusCodes.Status404NotFound ? StatusCode.NotFound : StatusCode.PermissionDenied;
                throw new RpcException(new Status(code, check.Message ?? "You don't have access to do that."));
            }

            if (!await _revisions.BranchExistsAsync(projectId, branchId, ct))
                throw new RpcException(new Status(StatusCode.NotFound, "The workspace's branch no longer exists. Run 'vx branch list' to see the project's branches."));
        }

        private static Guid ParseId(string value, string what)
        {
            return Guid.TryParse(value, out Guid id) ? id : throw Invalid($"Invalid {what} id.");
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
