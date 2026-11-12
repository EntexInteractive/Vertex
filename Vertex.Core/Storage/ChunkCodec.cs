// Copyright 2026 Entex Interactive

using System.Security.Cryptography;
using ZstdSharp;

namespace Vertex.Core.Storage
{
    /// <summary>
    /// Compresses chunks for transfer/storage and verifies them after decompression. Uses the managed zstd
    /// port, so it runs on any container image without native libraries.
    /// </summary>
    public static class ChunkCodec
    {
        public const int CompressionLevel = 3;

        /// <summary>
        /// Largest compressed form of a maximum-size chunk (incompressible data grows slightly).
        /// </summary>
        public static readonly int MaximumCompressedSize = Compressor.GetCompressBound(ContentChunker.MaximumChunkSize);

        [ThreadStatic] private static Compressor? _compressor;
        [ThreadStatic] private static Decompressor? _decompressor;

        public static byte[] Compress(ReadOnlySpan<byte> raw)
        {
            _compressor ??= new Compressor(CompressionLevel);
            return _compressor.Wrap(raw).ToArray();
        }

        /// <summary>
        /// Decompresses into <paramref name="destination"/> (sized to the declared raw length) and checks that the
        /// result has exactly that length and hashes to <paramref name="id"/>. The fixed destination bounds the
        /// output, so malicious input cannot expand without limit.
        /// </summary>
        /// <exception cref="InvalidDataException">The data is corrupt or does not match its id.</exception>
        public static void DecompressAndVerify(ReadOnlySpan<byte> id, ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            _decompressor ??= new Decompressor();
            int written;
            try
            {
                written = _decompressor.Unwrap(compressed, destination);
            }
            catch (Exception exception) when (exception is ZstdException or ArgumentException)
            {
                throw new InvalidDataException("Chunk data is corrupt or larger than its declared size.", exception);
            }

            if (written != destination.Length) throw new InvalidDataException("Chunk size does not match its declared size.");

            Span<byte> hash = stackalloc byte[ContentId.HashSize];
            SHA256.HashData(destination, hash);
            if (!hash.SequenceEqual(id)) throw new InvalidDataException("Chunk content does not match its id.");
        }
    }
}