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
    /// A lock on the workspace's branch, as last fetched from the server.
    /// </summary>
    /// <param name="PathKey">Lower-case path, as the server compares paths.</param>
    /// <param name="Path">Path relative to the workspace root, with <c>/</c> separators.</param>
    /// <param name="Mine">Held by this workspace.</param>
    /// <param name="Username">The holder.</param>
    /// <param name="Host">The holder's machine.</param>
    /// <param name="CreatedAt">When it was locked (Unix milliseconds).</param>
    /// <param name="MadeReadOnly">This workspace made the file read-only because of the lock, so it makes it writable
    /// again on release. Files the user made read-only themselves are never touched.</param>
    public sealed record LockEntry(string PathKey, string Path, bool Mine, string Username, string Host, long CreatedAt, bool MadeReadOnly);

    /// <summary>
    /// The workspace's local SQLite index (<c>.vx/index.db</c>). It caches each file's content id against its
    /// size and timestamp, so unchanged files are never re-hashed, and records the content each tracked file had
    /// at the last push or sync, which is what changes are measured against.
    /// </summary>
    public sealed class WorkspaceIndex : IDisposable
    {
        private const int SchemaVersion = 4;

        // Case-insensitive lookups by path, for checking single files without reading the whole index.
        private const string PathIndexSql = "CREATE INDEX IF NOT EXISTS files_path_nocase ON files (path COLLATE NOCASE);";

        /// <summary>
        /// The last project event seq the workspace's lock mirror has applied, stored in the <c>meta</c> table. (The
        /// older lock-only seq was stored as <c>lock_seq</c>; it's ignored, so such a workspace reloads its locks once.)
        /// </summary>
        public const string EventSeqKey = "event_seq";

        /// <summary>
        /// The last revision <c>vx sync</c> brought the workspace up to, stored in the <c>meta</c> table.
        /// </summary>
        public const string SyncedRevisionKey = "synced_revision";

        private const string MetaTableSql = "CREATE TABLE IF NOT EXISTS meta (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;";

        private const string LocksTableSql = "CREATE TABLE IF NOT EXISTS locks (path_key TEXT NOT NULL PRIMARY KEY, path TEXT NOT NULL, mine INTEGER NOT NULL, " +
                                             "username TEXT NOT NULL, host TEXT NOT NULL, created_at INTEGER NOT NULL, made_read_only INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;";

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
                                         {MetaTableSql}
                                         {LocksTableSql}
                                         {PathIndexSql}
                                         PRAGMA user_version = {SchemaVersion};
                                         """);
                }
                else if (version < SchemaVersion)
                {
                    // Upgrade in place: version 2 added the meta table, version 3 the locks table, version 4 the
                    // case-insensitive path index.
                    Execute(connection, $"{MetaTableSql} {LocksTableSql} {PathIndexSql} PRAGMA user_version = {SchemaVersion};");
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

        /// <summary>
        /// The rows for some paths, matched case-insensitively (the way the scanner compares paths on Windows and
        /// macOS), and every row under <paramref name="folders"/>.
        /// </summary>
        public List<IndexEntry> Find(IEnumerable<string> paths, IEnumerable<string> folders)
        {
            List<IndexEntry> entries = new();
            using (SqliteCommand command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT path, size, last_write, file_id, base_id, indexed_at FROM files WHERE path = $path COLLATE NOCASE;";
                SqliteParameter path = command.Parameters.Add("$path", SqliteType.Text);
                command.Prepare();
                foreach (string value in paths)
                {
                    path.Value = value;
                    ReadEntries(command, entries);
                }
            }

            using (SqliteCommand command = _connection.CreateCommand())
            {
                // A range on the case-insensitive index: every path starting with "folder/".
                command.CommandText = "SELECT path, size, last_write, file_id, base_id, indexed_at FROM files " +
                                      "WHERE path COLLATE NOCASE >= $from AND path COLLATE NOCASE < $to;";
                SqliteParameter from = command.Parameters.Add("$from", SqliteType.Text);
                SqliteParameter to = command.Parameters.Add("$to", SqliteType.Text);
                command.Prepare();
                foreach (string folder in folders)
                {
                    from.Value = folder + "/";
                    to.Value = folder + "0"; // '0' sorts right after '/'.
                    ReadEntries(command, entries);
                }
            }

            return entries;
        }

        private static void ReadEntries(SqliteCommand command, List<IndexEntry> entries)
        {
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new IndexEntry(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5)));
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

        /// <summary>
        /// Reads a value from the <c>meta</c> table.
        /// </summary>
        /// <returns>The value, or null when it was never set.</returns>
        public string? GetMeta(string key)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }

        public void SetMeta(string key, string value)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        public List<LockEntry> ReadLocks()
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT path_key, path, mine, username, host, created_at, made_read_only FROM locks;";
            using SqliteDataReader reader = command.ExecuteReader();

            List<LockEntry> locks = new();
            while (reader.Read())
            {
                locks.Add(new LockEntry(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6) != 0));
            }

            return locks;
        }

        /// <summary>
        /// Removes the locks with keys in <paramref name="deletes"/>, inserts or replaces <paramref name="upserts"/>
        /// and stores the lock seq, in one transaction.
        /// </summary>
        public void ApplyLocks(IEnumerable<LockEntry> upserts, IEnumerable<string> deletes, long lockSeq)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using (SqliteCommand delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM locks WHERE path_key = $key;";
                SqliteParameter key = delete.Parameters.Add("$key", SqliteType.Text);
                delete.Prepare();
                foreach (string deleted in deletes)
                {
                    key.Value = deleted;
                    delete.ExecuteNonQuery();
                }
            }

            using (SqliteCommand upsert = _connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = "INSERT OR REPLACE INTO locks (path_key, path, mine, username, host, created_at, made_read_only) " +
                                     "VALUES ($key, $path, $mine, $username, $host, $created_at, $made_read_only);";

                SqliteParameter key = upsert.Parameters.Add("$key", SqliteType.Text);
                SqliteParameter path = upsert.Parameters.Add("$path", SqliteType.Text);
                SqliteParameter mine = upsert.Parameters.Add("$mine", SqliteType.Integer);
                SqliteParameter username = upsert.Parameters.Add("$username", SqliteType.Text);
                SqliteParameter host = upsert.Parameters.Add("$host", SqliteType.Text);
                SqliteParameter createdAt = upsert.Parameters.Add("$created_at", SqliteType.Integer);
                SqliteParameter madeReadOnly = upsert.Parameters.Add("$made_read_only", SqliteType.Integer);
                upsert.Prepare();

                foreach (LockEntry entry in upserts)
                {
                    key.Value = entry.PathKey;
                    path.Value = entry.Path;
                    mine.Value = entry.Mine ? 1 : 0;
                    username.Value = entry.Username;
                    host.Value = entry.Host;
                    createdAt.Value = entry.CreatedAt;
                    madeReadOnly.Value = entry.MadeReadOnly ? 1 : 0;
                    upsert.ExecuteNonQuery();
                }
            }

            using (SqliteCommand meta = _connection.CreateCommand())
            {
                meta.Transaction = transaction;
                meta.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
                meta.Parameters.AddWithValue("$key", EventSeqKey);
                meta.Parameters.AddWithValue("$value", lockSeq.ToString());
                meta.ExecuteNonQuery();
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