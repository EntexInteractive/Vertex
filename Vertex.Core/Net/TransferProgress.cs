// Copyright 2026 Entex Interactive

namespace Vertex.Core.Net
{
    /// <summary>
    /// Running byte counts for an upload or download, for a progress bar. <see cref="Interfaces.FilesInterface"/>
    /// adds to it as each batch completes (never per chunk), and any thread may read it at any time.
    /// </summary>
    public sealed class TransferProgress
    {
        private long _totalBytes;
        private long _doneBytes;

        /// <summary>
        /// Bytes known to need transferring so far. An upload sets it once it knows which chunks the server is
        /// missing; a download grows it as each file's manifest arrives.
        /// </summary>
        public long TotalBytes => Interlocked.Read(ref _totalBytes);

        /// <summary>
        /// Bytes transferred, or for a download also reused from the local copy.
        /// </summary>
        public long DoneBytes => Interlocked.Read(ref _doneBytes);

        public void AddTotal(long bytes)
        {
            Interlocked.Add(ref _totalBytes, bytes);
        }

        public void AddDone(long bytes)
        {
            Interlocked.Add(ref _doneBytes, bytes);
        }
    }
}
