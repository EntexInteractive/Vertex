// Copyright 2026 Entex Interactive

using System.Threading.Channels;

namespace Vertex.Gui.Hosting
{
    /// <summary>
    /// One event pushed to the UI over <c>GET /api/events</c>, e.g. a lock conflict or transfer progress.
    /// </summary>
    /// <param name="Type">What happened, dotted like <c>workspace.lockConflict</c>. The UI switches on it.</param>
    /// <param name="Data">The event's payload, serialized as JSON.</param>
    public sealed record HostEvent(string Type, object? Data);

    /// <summary>
    /// Fans host events out to every open event stream. Each subscriber gets its own bounded queue, so a stalled page
    /// drops its oldest events instead of holding up the code that publishes them.
    /// </summary>
    public sealed class HostEvents
    {
        private const int QueueCapacity = 1024;

        private readonly Lock _gate = new();
        private readonly List<Channel<HostEvent>> _subscribers = [];

        public void Publish(string type, object? data = null)
        {
            HostEvent hostEvent = new(type, data);
            lock (_gate)
            {
                foreach (Channel<HostEvent> subscriber in _subscribers)
                {
                    subscriber.Writer.TryWrite(hostEvent);
                }
            }
        }

        /// <summary>
        /// Yields events published after the call until <paramref name="cancellationToken"/> fires.
        /// </summary>
        public async IAsyncEnumerable<HostEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Channel<HostEvent> channel = Channel.CreateBounded<HostEvent>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

            lock (_gate)
            {
                _subscribers.Add(channel);
            }

            try
            {
                await foreach (HostEvent hostEvent in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return hostEvent;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _subscribers.Remove(channel);
                }
            }
        }
    }
}
