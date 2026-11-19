// Copyright 2026 Entex Interactive

using System.Runtime.CompilerServices;
using Grpc.Core;
using Vertex.Server;

namespace Vertex.Core.Net.Interfaces
{
    /// <summary>
    /// The project event stream: everything that happens in a project, pushed as it happens.
    /// </summary>
    public class EventsInterface : BaseInterface
    {
        public EventFeed.EventFeedClient EventFeedClient { get; }

        public EventsInterface(string url, string? token = null) : base(url, token)
        {
            EventFeedClient = new EventFeed.EventFeedClient(GrpcChannel);
        }

        /// <summary>
        /// Streams the branch's events after <paramref name="afterSeq"/> (0 for only new ones), with heartbeats, until
        /// the server ends the stream or <paramref name="ct"/> is cancelled. Subscribe again with the last seq to
        /// continue without missing anything.
        /// </summary>
        public async IAsyncEnumerable<EventMessage> SubscribeAsync(Guid projectId, Guid branchId, long afterSeq, [EnumeratorCancellation] CancellationToken ct = default)
        {
            SubscribeRequest request = new() { ProjectId = projectId.ToString(), BranchId = branchId.ToString(), AfterSeq = afterSeq };
            using AsyncServerStreamingCall<EventMessage> call = EventFeedClient.Subscribe(request, cancellationToken: ct);
            await foreach (EventMessage message in call.ResponseStream.ReadAllAsync(ct)) yield return message;
        }
    }
}
