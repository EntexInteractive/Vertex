// Copyright 2026 Entex Interactive

using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Vertex.Server;

namespace Vertex.Core.Storage
{
    /// <summary>
    /// Content identifiers shared by the client and server. Chunk ids are the SHA-256 of the raw chunk bytes;
    /// a file id is the SHA-256 of its ordered chunk list (a Merkle-style hash), so anyone holding the
    /// manifest can recompute and verify it without reading the file content.
    /// </summary>
    public static class ContentId
    {
        public const int HashSize = 32;
        private static ReadOnlySpan<byte> FileIdDomain => "vx-file-v1\0"u8;

        public static string ComputeFileId(IEnumerable<(byte[] Id, long Size)> chunks)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(FileIdDomain);
            Span<byte> size = stackalloc byte[sizeof(long)];
            foreach ((byte[] id, long length) in chunks)
            {
                if (id.Length != HashSize) throw new InvalidDataException("Chunk id must be a SHA-256 hash.");
                hash.AppendData(id);
                BinaryPrimitives.WriteInt64LittleEndian(size, length);
                hash.AppendData(size);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        public static string ComputeFileId(FileManifest manifest)
        {
            return ComputeFileId(manifest.Chunks.Select(c => (c.Id.ToByteArray(), c.Size)));
        }

        public static FileManifest ToManifest(this ChunkedFile file)
        {
            FileManifest manifest = new() { Size = file.Size };
            foreach (FileChunk chunk in file.Chunks)
                manifest.Chunks.Add(new ChunkRef { Id = ByteString.CopyFrom(chunk.Id), Size = chunk.Length });

            return manifest;
        }

        public static bool IsValidFileId(string? fileId)
        {
            return fileId is { Length: HashSize * 2 } && fileId.All(char.IsAsciiHexDigit);
        }

        public static string ToHex(ReadOnlySpan<byte> id)
        {
            return Convert.ToHexStringLower(id);
        }
    }
}