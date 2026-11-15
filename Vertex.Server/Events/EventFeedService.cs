// Copyright 2026 Entex Interactive

using System.Security.Claims;
using Grpc.Core;
using Vertex.Core.Security;
using Vertex.Server.Access;
using Vertex.Server.History;

namespace Vertex.Server.Events
{
    /// <summary>
    /// gRPC endpoint that streams a project's events to clients. See Events.proto.
    /// </summary>
    public class EventFeedService : EventFeed.EventFeedBase
    {
        /// <summary>A heartbeat at least this often keeps proxies (Cloudflare: ~100 s idle) from closing the stream.</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        /// <summary>Streams end after this long so clients reconnect and spread across instances.</summary>
        private static readonly TimeSpan MaximumStreamDuration = TimeSpan.FromMinutes(30);

        private readonly ILogger<EventFeedService> _logger;
        private readonly INotificationService _notifications;
        private readonly EventStore _store;
        private readonly AccessService _access;
        private readonly RevisionsRepository _revisions;

        public EventFeedService(ILogger<EventFeedService> logger, INotificationService notifications, EventStore store, AccessService access, RevisionsRepository revisions)
        {
            _logger = logger;
            _notifications = notifications;
            _store = store;
            _access = access;
            _revisions = revisions;
        }

        public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<EventMessage> responseStream, ServerCallContext context)
        {
            ClaimsPrincipal user = context.GetHttpContext().User;
            Guid projectId = ProjectCalls.ParseId(request.ProjectId, "project");
            Guid branchId = ProjectCalls.ParseId(request.BranchId, "branch");
            await ProjectCalls.RequireAsync(_access, _revisions, user, projectId, branchId, AccessLevel.Read, context.CancellationToken);

            // Start from the client's seq when the events after it are still kept; otherwise it has to reload its state.
            (long latest, bool resumable) = await _notifications.PrepareAsync(projectId, request.AfterSeq, context.CancellationToken);
            long cursor = request.AfterSeq;
            if (cursor <= 0)
            {
                cursor = latest;
            }
            else if (!resumable)
            {
                cursor = latest;
                await responseStream.WriteAsync(new EventMessage { Seq = cursor, Resync = new Resync() });
            }

            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            lifetime.CancelAfter(MaximumStreamDuration);
            CancellationToken ct = lifetime.Token;

            IAsyncEnumerator<ProjectEvent> events = _notifications.SubscribeAsync(projectId, cursor, ct).GetAsyncEnumerator(ct);
            Task<bool> next = events.MoveNextAsync().AsTask();
            try
            {
                while (true)
                {
                    Task heartbeat = Task.Delay(HeartbeatInterval, ct);
                    if (await Task.WhenAny(next, heartbeat) == next)
                    {
                        if (!await next) return;
                        ProjectEvent projectEvent = events.Current;
                        cursor = projectEvent.Seq;
                        if (IsVisible(projectEvent, branchId)) await responseStream.WriteAsync(ToMessage(projectEvent), ct);
                        next = events.MoveNextAsync().AsTask();
                        continue;
                    }

                    if (ct.IsCancellationRequested) return;

                    // Access can be revoked while a stream is open; the check is cached, so this is cheap.
                    AccessCheck check = await _access.CheckAsync(user, projectId, AccessLevel.Read, ct);
                    if (!check.Allowed) throw new RpcException(new Status(StatusCode.PermissionDenied, check.Message ?? "You no longer have access to this project."));

                    await responseStream.WriteAsync(new EventMessage { Seq = cursor, Heartbeat = new Heartbeat() }, ct);
                }
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                // The stream reached its maximum duration; the client subscribes again from its seq.
            }
            catch (OperationCanceledException)
            {
                // The client went away.
            }
            finally
            {
                await lifetime.CancelAsync();
                try
                {
                    await next;
                }
                catch (OperationCanceledException)
                {
                    // Expected once cancelled.
                }

                await events.DisposeAsync();
            }
        }

        /// <summary>
        /// Lock and revision events belong to one branch; a subscriber only sees its own branch's.
        /// </summary>
        private static bool IsVisible(ProjectEvent projectEvent, Guid branchId)
        {
            return projectEvent.BranchId is null || projectEvent.BranchId == branchId;
        }

        internal static EventMessage ToMessage(ProjectEvent projectEvent)
        {
            EventMessage message = new()
            {
                Seq = projectEvent.Seq,
                Type = projectEvent.Type,
                Username = projectEvent.Username,
                WorkspaceId = projectEvent.WorkspaceId?.ToString() ?? string.Empty,
                Host = projectEvent.Host ?? string.Empty,
                CreatedAt = projectEvent.CreatedAt,
                BranchId = projectEvent.BranchId?.ToString() ?? string.Empty
            };

            switch (projectEvent.Type)
            {
                case EventTypes.LockLocked:
                case EventTypes.LockReleased:
                    LockPayload lockPayload = projectEvent.Read<LockPayload>();
                    LockChange change = new() { Released = projectEvent.Type == EventTypes.LockReleased, Reason = lockPayload.Reason ?? string.Empty };
                    change.Paths.AddRange(lockPayload.Paths);
                    message.Lock = change;
                    break;
                case EventTypes.RevisionCreated:
                    RevisionPayload revision = projectEvent.Read<RevisionPayload>();
                    message.Revision = new RevisionCreated { Revision = revision.Revision, Message = revision.Message, Files = revision.Files };
                    break;
            }

            return message;
        }
    }
}
