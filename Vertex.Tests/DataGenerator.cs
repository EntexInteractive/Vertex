// Copyright 2026 Entex Interactive

using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Vertex.Core.Storage;

namespace Vertex.Tests
{
    /// <summary>
    /// Writes test data to disk: large assets and game-like trees of small files. Content comes from a fast seeded
    /// generator (SplitMix64), unique per run so nothing deduplicates against earlier runs unless a scenario wants it.
    /// </summary>
    public sealed class DataGenerator
    {
        private const int BufferSize = 4 * 1024 * 1024;

        private static readonly string[] Areas =
        [
            "Content/Maps", "Content/Characters", "Content/Props", "Content/Materials", "Content/UI", "Source/Runtime",
            "Source/Editor", "Config", "Docs"
        ];

        private static readonly string[] Extensions = ["umap", "uasset", "uasset", "uasset", "uasset", "cpp", "h", "ini", "md"];

        private const int FilesPerFolder = 200;

        private readonly ulong _seed;
        private long _stream;

        public DataGenerator(ulong seed)
        {
            _seed = seed;
        }

        /// <summary>
        /// A content stream id no other file in this run uses.
        /// </summary>
        public ulong NextStream()
        {
            return (ulong)Interlocked.Increment(ref _stream) + 1_000_000_000UL;
        }

        /// <summary>
        /// Writes <paramref name="size"/> bytes of stream <paramref name="stream"/>. Incompressible content is the worst
        /// case for zstd (like already-compressed textures and audio); compressible content packs to about half.
        /// </summary>
        public void WriteFile(string path, long size, ulong stream, bool compressible)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(BufferSize, Math.Max(size, 8)));
            try
            {
                using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.None, size);
                ulong state = Mix(_seed ^ Mix(stream));
                long offset = 0;
                while (offset < size)
                {
                    int length = (int)Math.Min(buffer.Length, size - offset);
                    Fill(buffer.AsSpan(0, length), ref state, compressible);
                    RandomAccess.Write(handle, buffer.AsSpan(0, length), offset);
                    offset += length;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Writes large assets, alternating incompressible and compressible content.
        /// </summary>
        public IReadOnlyList<string> WriteAssets(string folder, int count, long size, CancellationToken ct)
        {
            string[] paths = Enumerable.Range(0, count).Select(i => Path.Combine(folder, $"Asset_{i:D3}.{(i % 2 == 0 ? "uasset" : "ubulk")}")).ToArray();
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = ct },
                i => WriteFile(paths[i], size, (ulong)i, i % 2 == 1));
            return paths;
        }

        /// <summary>
        /// Writes a tree of <paramref name="count"/> small files under <paramref name="root"/>, laid out like a game
        /// project (about 10 KB average, most under 4 KB, a few up to 128 KB).
        /// </summary>
        /// <returns>Paths relative to <paramref name="root"/>, with '/' separators.</returns>
        public IReadOnlyList<string> WriteTree(string root, int count, CancellationToken ct)
        {
            string[] relative = new string[count];
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, i =>
            {
                relative[i] = TreePath(i);
                WriteFile(Path.Combine(root, relative[i]), SmallFileSize(i), 500_000_000UL + (ulong)i, true);
            });

            return relative;
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="destination"/> and overwrites a contiguous
        /// <paramref name="fraction"/> of it in the middle, like re-saving an asset after a local edit.
        /// </summary>
        public void Mutate(string source, string destination, double fraction)
        {
            File.Copy(source, destination, true);
            long size = new FileInfo(destination).Length;
            long length = Math.Max(1, (long)(size * fraction));
            long offset = (long)(size * 0.4);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using SafeFileHandle handle = File.OpenHandle(destination, FileMode.Open, FileAccess.Write);
                ulong state = Mix(_seed ^ Mix(NextStream()));
                long written = 0;
                while (written < length)
                {
                    int count = (int)Math.Min(buffer.Length, length - written);
                    Fill(buffer.AsSpan(0, count), ref state, false);
                    RandomAccess.Write(handle, buffer.AsSpan(0, count), offset + written);
                    written += count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Chunks files in parallel, the way <c>vx submit</c> hashes changed files before uploading.
        /// </summary>
        public static ChunkedFile[] Chunk(IReadOnlyList<string> paths, CancellationToken ct)
        {
            ChunkedFile[] chunked = new ChunkedFile[paths.Count];
            Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                i => chunked[i] = ContentChunker.ChunkFile(paths[i], ct));
            return chunked;
        }

        /// <summary>
        /// Rough disk space a tree of <paramref name="count"/> files takes.
        /// </summary>
        public static long EstimateTreeBytes(int count)
        {
            return count * 12L * 1024;
        }

        private static string TreePath(int index)
        {
            int folder = index / FilesPerFolder;
            int area = folder % Areas.Length;
            return $"{Areas[area]}/Group{folder:D4}/File{index:D6}.{Extensions[area]}";
        }

        private long SmallFileSize(int index)
        {
            ulong roll = Mix(_seed ^ (0xA5A5UL + (ulong)index));
            int bucket = (int)(roll % 100);
            ulong spread = roll >> 8;
            if (bucket < 70) return 512 + (long)(spread % (4096 - 512));
            if (bucket < 95) return 4096 + (long)(spread % (32 * 1024 - 4096));
            return 32 * 1024 + (long)(spread % (96 * 1024));
        }

        private static void Fill(Span<byte> buffer, ref ulong state, bool compressible)
        {
            Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(buffer);
            for (int i = 0; i < words.Length; i++)
            {
                ulong value = Next(ref state);
                // 16 symbols per byte: about 4 bits of entropy, so zstd halves it, like text or uncompressed meshes.
                words[i] = compressible ? (value & 0x0F0F0F0F0F0F0F0FUL) + 0x6161616161616161UL : value;
            }

            for (int i = words.Length * sizeof(ulong); i < buffer.Length; i++)
                buffer[i] = compressible ? (byte)(0x61 + (Next(ref state) & 0x0F)) : (byte)Next(ref state);
        }

        private static ulong Next(ref ulong state)
        {
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static ulong Mix(ulong value)
        {
            return Next(ref value);
        }
    }
}
