// Copyright 2026 Entex Interactive

using System.Data;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using StackExchange.Redis;

namespace Vertex.Server.Database
{
    /// <summary>
    /// Represents a PostgresSQL <see cref="IDatabase"/> instance.
    /// </summary>
    public class PostgresDatabase : IHealthCheck
    {
        private readonly string _connectionString;

        /// <summary>
        /// Initializes a new instance of the <see cref="PostgresDatabase"/> class.
        /// </summary>
        public PostgresDatabase(ServerSettings settings)
        {
            _connectionString = settings.PostgresConnectionString;
        }

        /// <inheritdoc />
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
        {
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                using IDbConnection connection = CreateConnection();
                await connection.ExecuteAsync("SELECT 1;", cancellationToken);
                sw.Stop();

                return HealthCheckResult.Healthy(data: new Dictionary<string, object> { ["Latency"] = sw.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return HealthCheckResult.Unhealthy("Unable to ping Postgres", ex);
            }
        }

        /// <summary>
        /// Initializes a new instance of <see cref="IDbConnection"/>.
        /// </summary>
        public IDbConnection CreateConnection()
        {
            return new NpgsqlConnection(_connectionString);
        }
    }
}