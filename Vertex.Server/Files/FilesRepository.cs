// Copyright 2026 Entex Interactive

using Google.Protobuf;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Vertex.Core.Storage;
using Vertex.Server.Database;
using Vertex.Server.Telemetry;

namespace Vertex.Server.FileServices
{
    /// <summary>
    /// The content index (<c>files.chunks</c>, <c>files.manifests</c>): which chunks and file manifests are stored. S3
    /// holds the chunk bytes; this answers "is it already uploaded?" for a whole request in one query and records a
    /// request's uploads in one commit, instead of one S3 call (and one durable object write) per id.
    /// </summary>
    /// <remarks>
    /// Everything indexed is immutable and content-addressed, so rows are only inserted, inserting twice is a no-op, and
    /// a positive answer never goes stale (until garbage collection exists, which must clear the cache). Only positive
    /// answers are cached; a miss always asks the database, so an upload on another instance is seen at once.
    /// </remarks>
    public sealed class FilesRepository
    {
        private static readonly MemoryCacheEntryOptions CacheEntryOptions = new() { SlidingExpiration = TimeSpan.FromHours(1), Size = 1 };

        private readonly PostgresDatabase _database;
        private readonly MemoryCache _known = new(new MemoryCacheOptions { SizeLimit = 1_000_000 });

        public FilesRepository(ServerSettings settings)
        {
            _database = new PostgresDatabase(settings);
        }

        /// <summary>
        /// The raw sizes of the chunks that are stored, by lowercase hex id. Ids not in the result are not stored.
        /// </summary>
        public async Task<Dictionary<string, long>> GetChunkSizesAsync(IReadOnlyCollection<byte[]> ids, CancellationToken ct)
        {
            Dictionary<string, long> sizes = new(StringComparer.Ordinal);
            List<byte[]> unknown = new();
            foreach (byte[] id in ids)
            {
                string hex = ContentId.ToHex(id);
                if (_known.TryGetValue(ChunkKey(hex), out long size)) sizes[hex] = size;
                else unknown.Add(id);
            }

            Count(ids.Count - unknown.Count, unknown.Count);
            if (unknown.Count == 0) return sizes;

            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(ct);
            await using NpgsqlCommand command = new("SELECT id, size FROM files.chunks WHERE id = ANY(@Ids);", connection);
            command.Parameters.Add(new NpgsqlParameter<byte[][]>("Ids", unknown.ToArray()));
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string hex = ContentId.ToHex(reader.GetFieldValue<byte[]>(0));
                long size = reader.GetInt32(1);
                sizes[hex] = size;
                _known.Set(ChunkKey(hex), size, CacheEntryOptions);
            }

            return sizes;
        }

        /// <summary>
        /// The file ids (lowercase hex) whose manifests are stored.
        /// </summary>
        public async Task<HashSet<string>> GetStoredManifestsAsync(IReadOnlyCollection<string> fileIds, CancellationToken ct)
        {
            HashSet<string> stored = new(StringComparer.Ordinal);
            List<string> unknown = new();
            foreach (string fileId in fileIds)
            {
                if (_known.TryGetValue(ManifestKey(fileId), out _)) stored.Add(fileId);
                else unknown.Add(fileId);
            }

            Count(fileIds.Count - unknown.Count, unknown.Count);
            if (unknown.Count == 0) return stored;

            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(ct);
            await using NpgsqlCommand command = new("SELECT file_id FROM files.manifests WHERE file_id = ANY(@Ids);", connection);
            command.Parameters.Add(new NpgsqlParameter<string[]>("Ids", unknown.ToArray()));
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string fileId = reader.GetString(0);
                stored.Add(fileId);
                _known.Set(ManifestKey(fileId), true, CacheEntryOptions);
            }

            return stored;
        }

        /// <summary>
        /// Records chunks whose bytes are now in S3, in one statement and one commit.
        /// </summary>
        public async Task AddChunksAsync(IReadOnlyCollection<(byte[] Id, long Size)> chunks, CancellationToken ct)
        {
            if (chunks.Count == 0) return;
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(ct);
            await using NpgsqlCommand command = new("INSERT INTO files.chunks (id, size) SELECT * FROM unnest(@Ids::BYTEA[], @Sizes::INTEGER[]) ON CONFLICT (id) DO NOTHING;", connection);
            command.Parameters.Add(new NpgsqlParameter<byte[][]>("Ids", chunks.Select(c => c.Id).ToArray()));
            command.Parameters.Add(new NpgsqlParameter<int[]>("Sizes", chunks.Select(c => (int)c.Size).ToArray()));
            await command.ExecuteNonQueryAsync(ct);

            foreach ((byte[] id, long size) in chunks) _known.Set(ChunkKey(ContentId.ToHex(id)), size, CacheEntryOptions);
        }

        /// <summary>
        /// Stores file manifests (already verified against their ids), in one statement and one commit.
        /// </summary>
        public async Task AddManifestsAsync(IReadOnlyCollection<(string FileId, FileManifest Manifest)> manifests, CancellationToken ct)
        {
            if (manifests.Count == 0) return;
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(ct);
            await using NpgsqlCommand command = new("INSERT INTO files.manifests (file_id, size, manifest) " +
                                                    "SELECT * FROM unnest(@Ids::TEXT[], @Sizes::BIGINT[], @Manifests::BYTEA[]) ON CONFLICT (file_id) DO NOTHING;", connection);
            command.Parameters.Add(new NpgsqlParameter<string[]>("Ids", manifests.Select(m => m.FileId).ToArray()));
            command.Parameters.Add(new NpgsqlParameter<long[]>("Sizes", manifests.Select(m => m.Manifest.Size).ToArray()));
            command.Parameters.Add(new NpgsqlParameter<byte[][]>("Manifests", manifests.Select(m => m.Manifest.ToByteArray()).ToArray()));
            await command.ExecuteNonQueryAsync(ct);

            foreach ((string fileId, FileManifest _) in manifests) _known.Set(ManifestKey(fileId), true, CacheEntryOptions);
        }

        /// <summary>
        /// A stored manifest, or null when this index doesn't have it (it may predate the index; see
        /// <see cref="Storage.IContentStore.GetManifestAsync"/>).
        /// </summary>
        public async Task<FileManifest?> GetManifestAsync(string fileId, CancellationToken ct)
        {
            await using NpgsqlConnection connection = (NpgsqlConnection)_database.CreateConnection();
            await connection.OpenAsync(ct);
            await using NpgsqlCommand command = new("SELECT manifest FROM files.manifests WHERE file_id = @Id;", connection);
            command.Parameters.Add(new NpgsqlParameter<string>("Id", fileId));
            return await command.ExecuteScalarAsync(ct) is byte[] data ? FileManifest.Parser.ParseFrom(data) : null;
        }

        private static string ChunkKey(string hex)
        {
            return "c:" + hex;
        }

        private static string ManifestKey(string fileId)
        {
            return "m:" + fileId;
        }

        private static void Count(int hits, int misses)
        {
            if (hits > 0) VertexMetrics.StorageCache.Add(hits, VertexMetrics.Tag("result", "hit"));
            if (misses > 0) VertexMetrics.StorageCache.Add(misses, VertexMetrics.Tag("result", "miss"));
        }
    }
}
