// Copyright 2026 Entex Interactive

using System.Security.Claims;
using Grpc.Core;
using Vertex.Core.Security;
using Vertex.Server.Access;
using Vertex.Server.Auth;
using Vertex.Server.Database;
using Vertex.Server.Events;

namespace Vertex.Server.History
{
    /// <summary>
    /// Checks and messages shared by the gRPC services that work on a project's branch (revisions and locks).
    /// </summary>
    internal static class ProjectCalls
    {
        /// <summary>
        /// Throws unless the caller has <paramref name="level"/> in the project and the branch belongs to it.
        /// </summary>
        public static async Task RequireAsync(AccessService access, RevisionsRepository revisions, ClaimsPrincipal user, Guid projectId, Guid branchId, AccessLevel level, CancellationToken ct)
        {
            AccessCheck check = await access.CheckAsync(user, projectId, level, ct);
            if (!check.Allowed)
            {
                StatusCode code = check.StatusCode == StatusCodes.Status404NotFound ? StatusCode.NotFound : StatusCode.PermissionDenied;
                throw new RpcException(new Status(code, check.Message ?? "You don't have access to do that."));
            }

            if (!await revisions.BranchExistsAsync(projectId, branchId, ct))
                throw new RpcException(new Status(StatusCode.NotFound, "The workspace's branch no longer exists. Run 'vx branch list' to see the project's branches."));
        }

        /// <summary>
        /// The shared key holding a project's latest event seq, which lets idle lock polls skip the database.
        /// </summary>
        public static string EventSeqKey(Guid projectId)
        {
            return $"events:seq:{projectId:N}";
        }

        /// <summary>
        /// How long the cached event seq lives. Short, so a lost update can only hide changes briefly.
        /// </summary>
        public static readonly TimeSpan EventSeqLifetime = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Caches a project's latest event seq for the lock poll's fast path.
        /// </summary>
        public static Task CacheEventSeqAsync(CacheContext cache, Guid projectId, long? seq)
        {
            return seq is long value ? cache.SetMaxAsync(EventSeqKey(projectId), value, EventSeqLifetime) : Task.CompletedTask;
        }

        /// <summary>
        /// After a commit that recorded events: caches the new seq and wakes everyone following the project.
        /// </summary>
        public static async Task AfterCommitAsync(CacheContext cache, INotificationService notifications, Guid projectId, long? seq)
        {
            if (seq is not long value) return;
            await CacheEventSeqAsync(cache, projectId, value);
            await notifications.NotifyAsync(projectId, value);
        }

        /// <summary>
        /// "Maps/City.umap is locked by alice on DESKTOP-7. ..." for a refused submit, naming the fix.
        /// </summary>
        public static string DescribeLockedByOthers(IReadOnlyList<LockRow> locks, Guid userId)
        {
            LockRow first = locks[0];
            string more = locks.Count > 1 ? $" (and {locks.Count - 1:N0} more locked file(s))" : "";
            if (first.UserId == userId)
            {
                return $"'{first.Path}' is locked by you in another workspace on {first.Host}{more}. Submit it from there, or if that workspace is gone, " +
                       $"run 'vx unlock --force \"{first.Path}\"'.";
            }

            return $"'{first.Path}' is locked by {first.Username} on {first.Host}{more}. Wait for them to submit it, " +
                   $"or run 'vx revert \"{first.Path}\"' to drop your change.";
        }

        public static Guid ParseId(string value, string what)
        {
            return Guid.TryParse(value, out Guid id) ? id : throw Invalid($"Invalid {what} id.");
        }

        public static RpcException Invalid(string message)
        {
            return new RpcException(new Status(StatusCode.InvalidArgument, message));
        }
    }
}
