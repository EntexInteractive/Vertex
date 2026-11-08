// Copyright 2026 Entex Interactive

using System.Data;
using System.Net;
using Vertex.Core.Data;
using Vertex.Server.Database;

namespace Vertex.Server.Diagnostics
{
    public class DiagnosticRepository
    {
        private readonly MySqlDatabase _database;
        private readonly DapperContext _dapper;

        public DiagnosticRepository(MySqlDatabase database, DapperContext dapper)
        {
            _database = database;
            _dapper = dapper;
        }

        /// <summary>
        /// Adds a request to the database.
        /// </summary>
        /// <param name="request"></param>
        public async Task<bool> AddRequestAsync(VRequest request)
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            string sql = @"INSERT INTO requests(received_at, uuid, method, endpoint, status, process_ms, length, cpu_usage, ram_usage) VALUES(@ReceivedAt, @Uuid, @Method, @Endpoint, @StatusCode, @ProcessMs, @Length, @CpuUsage, @RamUsage)";
            return await _dapper.ExecuteAsync(conn, sql, new { request.ReceivedAt, request.Uuid, request.Method, request.Endpoint, request.StatusCode, request.ProcessMs, request.Length, request.CpuUsage, request.RamUsage });
        }

        /// <summary>
        /// Gets the average request process time in the last 24 hours.
        /// </summary>
        /// <returns></returns>
        public async Task<double> GetProcessTimeAsync()
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            string sql = @"SELECT AVG(process_ms) FROM requests WHERE received_at >= (NOW(3) - INTERVAL 48 HOUR);";
            return await _dapper.QuerySingleAsync<double>(conn, sql, new { }, "request_avg_ms", 2);
        }

        public async Task<double> GetRequestLengthAsync()
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            string sql = @"SELECT AVG(length) FROM requests WHERE received_at >= (NOW(3) - INTERVAL 24 HOUR);";
            return await _dapper.QuerySingleAsync<double>(conn, sql, new { }, "request_avg_length", 2);
        }
    }
}