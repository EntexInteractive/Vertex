// Copyright 2026 Entex Interactive

using System.Data;
using System.Net;
using Vertex.Core.Data;
using Vertex.Server.Database;

namespace Vertex.Server.Diagnostics
{
    public class DiagnosticRepository
    {
        private readonly PostgresDatabase _database;
        private readonly DapperContext _dapper;

        public DiagnosticRepository(PostgresDatabase database, DapperContext dapper)
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
            const string sql = "INSERT INTO diagnostics.requests(received_at, auth_user, method, endpoint, status_code, process_ms, length) VALUES(@ReceivedAt, @AuthUser, @Method, @Endpoint, @StatusCode, @ProcessMs, @Length);";
            return await _dapper.ExecuteAsync(conn, sql, new { request.ReceivedAt, request.AuthUser, request.Method, request.Endpoint, request.StatusCode, request.ProcessMs, request.Length });
        }

        /// <summary>
        /// Gets the average request process time in the last 24 hours.
        /// </summary>
        /// <returns></returns>
        public async Task<int> GetProcessTimeAsync()
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "SELECT COALESCE(ROUND(AVG(process_ms)), 0)::INTEGER FROM diagnostics.requests WHERE received_at >= NOW() - INTERVAL '48 hours'";
            return await _dapper.QuerySingleAsync<int>(conn, sql, new { }, "request_avg_ms", 2);
        }

        public async Task<long> GetRequestLengthAsync()
        {
            Func<IDbConnection> conn = () => _database.CreateConnection();
            const string sql = "SELECT COALESCE(ROUND(AVG(length)), 0)::BIGINT FROM diagnostics.requests WHERE received_at >= NOW() - INTERVAL '48 hours';";
            return await _dapper.QuerySingleAsync<long>(conn, sql, new { }, "request_avg_length", 2);
        }
    }
}