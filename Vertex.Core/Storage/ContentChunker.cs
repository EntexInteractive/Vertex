// Copyright 2026 Entex Interactive

using System.Buffers;
using System.Security.Cryptography;

namespace Vertex.Core.Storage
{
    /// <summary>
    /// A chunk of a local file: where it is and what its content id is.
    /// </summary>
    public sealed record FileChunk(byte[] Id, long Offset, int Length);

    /// <summary>
    /// A local file split into content-defined chunks, with its content-addressed file id.
    /// </summary>
    public sealed class ChunkedFile
    {
        public required string Path { get; init; }
        public required string FileId { get; init; }
        public required long Size { get; init; }
        public required IReadOnlyList<FileChunk> Chunks { get; init; }
    }

    /// <summary>
    /// Splits content into chunks with FastCDC (content-defined chunking with a gear rolling hash and
    /// normalized chunk sizes). Cut points depend only on nearby content, so inserting or deleting bytes in a
    /// file only changes the chunks around the edit and the rest deduplicate against earlier versions.
    /// </summary>
    /// <remarks>
    /// The gear table and size parameters are part of the storage format: changing them changes every chunk
    /// and file id. Bump <see cref="ContentId"/>'s format version if they ever change.
    /// </remarks>
    public static class ContentChunker
    {
        public const int MinimumChunkSize = 256 * 1024;
        public const int AverageChunkSize = 1024 * 1024;
        public const int MaximumChunkSize = 4 * 1024 * 1024;

        // Normalized chunking: a stricter mask before the average size and a looser one after it pulls chunk
        // sizes toward the average. Masks use the high bits so each decision depends on the last 64 bytes.
        private const int AverageBits = 20; // log2(AverageChunkSize)
        private const ulong StrictMask = ulong.MaxValue << (64 - (AverageBits + 2));
        private const ulong LooseMask = ulong.MaxValue << (64 - (AverageBits - 2));

        private static readonly ulong[] Gear = CreateGearTable();

        /// <summary>
        /// Returns the length of the next chunk at the start of <paramref name="data"/>. The caller must supply at
        /// least <see cref="MaximumChunkSize"/> bytes unless the data reaches the end of the content.
        /// </summary>
        public static int FindCutPoint(ReadOnlySpan<byte> data)
        {
            int length = data.Length;
            if (length <= MinimumChunkSize) return length;
            if (length > MaximumChunkSize) length = MaximumChunkSize;
            int normal = Math.Min(AverageChunkSize, length);

            ulong hash = 0;
            int i = MinimumChunkSize;
            for (; i < normal; i++)
            {
                hash = (hash << 1) + Gear[data[i]];
                if ((hash & StrictMask) == 0) return i + 1;
            }

            for (; i < length; i++)
            {
                hash = (hash << 1) + Gear[data[i]];
                if ((hash & LooseMask) == 0) return i + 1;
            }

            return length;
        }

        /// <summary>
        /// Reads a file once, splitting it into chunks and hashing each chunk.
        /// </summary>
        public static ChunkedFile ChunkFile(string path, CancellationToken cancellationToken = default)
        {
            List<FileChunk> chunks = new();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(MaximumChunkSize * 2);
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
                int start = 0, end = 0;
                long offset = 0;
                bool endOfFile = false;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!endOfFile && end - start < MaximumChunkSize)
                    {
                        Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                        end -= start;
                        start = 0;
                        while (end < buffer.Length)
                        {
                            int read = stream.Read(buffer, end, buffer.Length - end);
                            if (read == 0)
                            {
                                endOfFile = true;
                                break;
                            }

                            end += read;
                        }
                    }

                    if (end == start) break;
                    ReadOnlySpan<byte> window = buffer.AsSpan(start, end - start);
                    int cut = FindCutPoint(window);
                    chunks.Add(new FileChunk(SHA256.HashData(window[..cut]), offset, cut));
                    offset += cut;
                    start += cut;
                }

                return new ChunkedFile { Path = path, Size = offset, Chunks = chunks, FileId = ContentId.ComputeFileId(chunks.Select(c => (c.Id, (long)c.Length))) };
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static ulong[] CreateGearTable()
        {
            // SplitMix64 with a fixed seed: deterministic on every platform and well distributed.
            ulong state = 0x5665727465780001UL;
            ulong[] table = new ulong[256];
            for (int i = 0; i < table.Length; i++)
            {
                ulong z = state += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                table[i] = z ^ (z >> 31);
            }

            return table;
        }
    }
}