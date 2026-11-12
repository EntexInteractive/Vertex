// Copyright 2026 Entex Interactive

using Grpc.Core;
using Vertex.Core.Storage;

namespace Vertex.Server.Storage
{
    /// <summary>
    /// Pod-wide limit on chunks being processed in memory at once, across every transfer on this instance.
    /// Sized from the memory actually available to the process (the container limit in Kubernetes), so adding
    /// concurrent transfers slows them down instead of getting the pod OOM-killed.
    /// </summary>
    /// <remarks>
    /// Transfers wait for a slot before reading their next chunk, which stops reading the gRPC stream and lets
    /// HTTP/2 flow control push back on the client. If no slot frees up within <see cref="MaximumWait"/>, the
    /// call fails with RESOURCE_EXHAUSTED so the client can retry, possibly against a less busy replica.
    /// </remarks>
    public sealed class TransferBudget : IDisposable
    {
        public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(30);

        /// <summary>Share of available memory reserved for chunk buffers.</summary>
        private const double MemoryShare = 0.25;

        private readonly SemaphoreSlim _slots;

        public int Slots { get; }

        public TransferBudget(ILogger<TransferBudget> logger)
        {
            // Each slot can hold a compressed chunk plus its decompressed copy.
            long perSlot = ChunkCodec.MaximumCompressedSize + ContentChunker.MaximumChunkSize;
            long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            Slots = (int)Math.Clamp((long)(available * MemoryShare) / perSlot, 4, 512);
            _slots = new SemaphoreSlim(Slots, Slots);
            logger.LogInformation("Transfer budget: {Slots} concurrent chunks ({AvailableMb} MB available memory)", Slots, available / (1024 * 1024));
        }

        /// <summary>
        /// Waits for a slot. Dispose the result to release it.
        /// </summary>
        /// <exception cref="RpcException">RESOURCE_EXHAUSTED when the server stays saturated.</exception>
        public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            if (!await _slots.WaitAsync(MaximumWait, cancellationToken))
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "Server is at transfer capacity; retry shortly."));

            return new Lease(_slots);
        }

        public void Dispose()
        {
            _slots.Dispose();
        }

        private sealed class Lease(SemaphoreSlim slots) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release();
            }
        }
    }
}