// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using Npgsql;

namespace Vertex.Server.Database
{
    /// <summary>
    /// Brings the PostgreSQL schema up to date at startup by applying the SQL files in <c>Database/Migrations/</c>
    /// (embedded as <c>NNNN_name.sql</c>) that the database hasn't run yet. Applied scripts are recorded in
    /// <c>public.schema_migrations</c>. Safe when several server instances start at once: they take turns through a
    /// PostgreSQL advisory lock, and only the first one does any work.
    /// </summary>
    public sealed partial class DatabaseMigrator
    {
        // Any fixed number works; every server instance must use the same one. ASCII "VXMIGRAT".
        private const long LockKey = 0x56584D4947524154;
        private const string JournalSchema = "public";
        private const string JournalTable = "schema_migrations";

        private readonly ServerSettings _settings;
        private readonly ILogger<DatabaseMigrator> _logger;

        /// <summary>
        /// Creates a migrator for the database in <see cref="ServerSettings.PostgresConnectionString"/>.
        /// </summary>
        public DatabaseMigrator(ServerSettings settings, ILogger<DatabaseMigrator> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// Applies every pending migration, each in its own transaction.
        /// </summary>
        /// <exception cref="InvalidOperationException">The database can't be reached, is newer than this server, or a
        /// migration failed. The message says what to do.</exception>
        public async Task MigrateAsync(CancellationToken cancellationToken = default)
        {
            Assembly assembly = typeof(DatabaseMigrator).Assembly;
            CheckMigrationNames(assembly);

            // The lock lives on its own connection and is released when that connection closes, even if this process
            // dies half-way, so a crashed instance can never leave the others waiting forever.
            await using NpgsqlConnection lockConnection = new(_settings.PostgresConnectionString);
            try
            {
                await lockConnection.OpenAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                NpgsqlConnectionStringBuilder target = new(_settings.PostgresConnectionString);
                throw new InvalidOperationException($"Can't connect to PostgreSQL at {target.Host}:{target.Port} ({ex.Message}). Check {ServerSettings.PostgresVariable}, or start the local database with 'docker compose up -d postgres'.", ex);
            }

            await using (NpgsqlCommand lockCommand = new("SELECT pg_advisory_lock(@key)", lockConnection))
            {
                lockCommand.Parameters.AddWithValue("key", LockKey);
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            UpgradeEngine engine = DeployChanges.To
                .PostgresqlDatabase(_settings.PostgresConnectionString)
                .WithScriptsEmbeddedInAssembly(assembly, IsMigration)
                .JournalToPostgresqlTable(JournalSchema, JournalTable)
                .WithTransactionPerScript()
                .WithVariablesDisabled()
                .LogTo(new UpgradeLog(_logger))
                .Build();

            // An older server must not run against a database that a newer one has already migrated: its queries
            // would hit a schema it doesn't understand.
            List<string> unknown = engine.GetExecutedButNotDiscoveredScripts();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException($"The database has migration {string.Join(", ", unknown)}, which this server doesn't know. It was upgraded by a newer Vertex server; update this server to that version or later.");
            }

            List<SqlScript> pending = engine.GetScriptsToExecute();
            if (pending.Count == 0)
            {
                _logger.LogDebug("Database schema is up to date");
                return;
            }

            _logger.LogInformation("Applying {Count} database migration(s): {Names}", pending.Count, string.Join(", ", pending.Select(s => s.Name)));
            Stopwatch stopwatch = Stopwatch.StartNew();
            DatabaseUpgradeResult result = engine.PerformUpgrade();
            if (!result.Successful)
            {
                throw new InvalidOperationException($"Database migration {result.ErrorScript?.Name ?? "(unknown)"} failed: {result.Error?.Message}. It was rolled back; fix it and restart the server.", result.Error);
            }

            _logger.LogInformation("Applied {Count} database migration(s) in {Elapsed} ms", result.Scripts.Count(), stopwatch.ElapsedMilliseconds);
            // Closing lockConnection releases the advisory lock.
        }

        private static bool IsMigration(string resourceName)
        {
            return MigrationName().IsMatch(resourceName);
        }

        /// <summary>
        /// Rejects two migrations with the same number, which would run in an order nobody chose.
        /// </summary>
        private static void CheckMigrationNames(Assembly assembly)
        {
            List<string> duplicates = assembly.GetManifestResourceNames()
                .Where(IsMigration)
                .GroupBy(name => name[..4])
                .Where(group => group.Count() > 1)
                .Select(group => string.Join(" and ", group))
                .ToList();

            if (duplicates.Count > 0)
            {
                throw new InvalidOperationException($"Migrations share a number: {string.Join("; ", duplicates)}. Renumber one of them.");
            }
        }

        // Resources are embedded under their bare file name (see Vertex.Server.csproj), which is also what the journal
        // records, so moving or renaming the namespace can't make applied migrations look new.
        [GeneratedRegex(@"^\d{4}_[A-Za-z0-9_]+\.sql$")]
        private static partial Regex MigrationName();

        /// <summary>
        /// Routes DbUp's chatter to debug and keeps warnings and errors visible.
        /// </summary>
        private sealed class UpgradeLog : IUpgradeLog
        {
            private readonly ILogger<DatabaseMigrator> _logger;

            public UpgradeLog(ILogger<DatabaseMigrator> logger)
            {
                _logger = logger;
            }

            public void LogTrace(string format, params object[] args)
            {
                _logger.LogTrace("{Message}", string.Format(format, args));
            }

            public void LogDebug(string format, params object[] args)
            {
                _logger.LogDebug("{Message}", string.Format(format, args));
            }

            public void LogInformation(string format, params object[] args)
            {
                _logger.LogDebug("{Message}", string.Format(format, args));
            }

            public void LogWarning(string format, params object[] args)
            {
                _logger.LogWarning("{Message}", string.Format(format, args));
            }

            public void LogError(string format, params object[] args)
            {
                _logger.LogError("{Message}", string.Format(format, args));
            }

            public void LogError(Exception ex, string format, params object[] args)
            {
                _logger.LogError(ex, "{Message}", string.Format(format, args));
            }
        }
    }
}
