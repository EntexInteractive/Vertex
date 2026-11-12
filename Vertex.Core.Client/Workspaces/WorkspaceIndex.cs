// Copyright 2026 Entex Interactive

using Microsoft.Data.Sqlite;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// One row of the workspace index.
    /// </summary>
    /// <param name="Path">Path relative to the workspace root, with <c>/</c> separators, spelled as first seen.</param>
    /// <param name="Size">File size when it was last hashed.</param>
    /// <param name="LastWriteTicks">Last write time (UTC ticks) when it was last hashed.</param>
    /// <param name="FileId">Content id of the file as it was when last hashed.</param>
    /// <param name="BaseId">Content id last pushed or synced, or null when the file is untracked.</param>
    /// <param name="IndexedAtTicks">When the file was hashed (UTC ticks), used to catch edits made within the
    /// file system's timestamp resolution.</param>
    public sealed record IndexEntry(string Path, long Size, long LastWriteTicks, string FileId, string? BaseId, long IndexedAtTicks);

    /// <summary>
    /// The workspace's local SQLite index (<c>.vx/index.db</c>). It caches each file's content id against its
    /// size and timestamp, so unchanged files are never re-hashed, and records the content each tracked file had
    /// at the last push or sync, which is what changes are measured against.
    /// </summary>
    public sealed class WorkspaceIndex : IDisposable
    {
        private const int SchemaVersion = 1;

        private readonly SqliteConnection _connection;

        private WorkspaceIndex(SqliteConnection connection)
        {
            _connection = connection;
        }

        public static WorkspaceIndex Open(string path)
        {
            SqliteConnectionStringBuilder builder = new()
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false // Release the file when disposed, so the .vx folder can be moved or deleted.
            };

            SqliteConnection connection = new(builder.ToString());
            try
            {
                connection.Open();

                // WAL lets the Agent and the CLI read while the other writes.
                Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
                long version = (long)Scalar(connection, "PRAGMA user_version;")!;
                if (version == 0)
                {
                    Execute(connection, $"""
                                         CREATE TABLE files (
                                             path        TEXT    NOT NULL PRIMARY KEY,
                                             size        INTEGER NOT NULL,
                                             last_write  INTEGER NOT NULL,
                                             file_id     TEXT    NOT NULL,
                                             base_id     TEXT    NULL,
                                             indexed_at  INTEGER NOT NULL
                                         ) WITHOUT ROWID;
                                         PRAGMA user_version = {SchemaVersion};
                                         """);
                }
                else if (version != SchemaVersion)
                {
                    throw new InvalidDataException($"The workspace index uses schema version {version}, but this version of Vertex supports {SchemaVersion}. Update Vertex.");
                }

                return new WorkspaceIndex(connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public List<IndexEntry> ReadAll()
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT path, size, last_write, file_id, base_id, indexed_at FROM files;";
            using SqliteDataReader reader = command.ExecuteReader();

            List<IndexEntry> entries = new();
            while (reader.Read())
            {
                entries.Add(new IndexEntry(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5)));
            }

            return entries;
        }

        /// <summary>
        /// Inserts or replaces <paramref name="upserts"/> and removes <paramref name="deletes"/>, in one transaction.
        /// </summary>
        public void Apply(IEnumerable<IndexEntry> upserts, IEnumerable<string> deletes)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using (SqliteCommand upsert = _connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                                     INSERT INTO files (path, size, last_write, file_id, base_id, indexed_at)
                                     VALUES ($path, $size, $last_write, $file_id, $base_id, $indexed_at)
                                     ON CONFLICT (path) DO UPDATE SET
                                         size = excluded.size, last_write = excluded.last_write, file_id = excluded.file_id,
                                         base_id = excluded.base_id, indexed_at = excluded.indexed_at;
                                     """;

                SqliteParameter path = upsert.Parameters.Add("$path", SqliteType.Text);
                SqliteParameter size = upsert.Parameters.Add("$size", SqliteType.Integer);
                SqliteParameter lastWrite = upsert.Parameters.Add("$last_write", SqliteType.Integer);
                SqliteParameter fileId = upsert.Parameters.Add("$file_id", SqliteType.Text);
                SqliteParameter baseId = upsert.Parameters.Add("$base_id", SqliteType.Text);
                SqliteParameter indexedAt = upsert.Parameters.Add("$indexed_at", SqliteType.Integer);
                upsert.Prepare();

                foreach (IndexEntry entry in upserts)
                {
                    path.Value = entry.Path;
                    size.Value = entry.Size;
                    lastWrite.Value = entry.LastWriteTicks;
                    fileId.Value = entry.FileId;
                    baseId.Value = (object?)entry.BaseId ?? DBNull.Value;
                    indexedAt.Value = entry.IndexedAtTicks;
                    upsert.ExecuteNonQuery();
                }
            }

            using (SqliteCommand delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM files WHERE path = $path;";
                SqliteParameter path = delete.Parameters.Add("$path", SqliteType.Text);
                delete.Prepare();

                foreach (string deleted in deletes)
                {
                    path.Value = deleted;
                    delete.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static object? Scalar(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
    }
}