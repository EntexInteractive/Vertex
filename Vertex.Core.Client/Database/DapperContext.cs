// Copyright 2026 Entex Interactive

using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Vertex.Core.Client.Database
{
    public class DapperContext
    {
        private readonly string _connectionString;

        /// <summary>
        /// Initializes a new instance of the <see cref="DapperContext"/> class.
        /// </summary>
        /// <param name="connectionString"></param>
        public DapperContext(string connectionString)
        {
            _connectionString = connectionString;
        }

        private IDbConnection CreateConnection()
        {
            return new SqliteConnection(_connectionString);
        }

        public async Task<int> ExecuteAsync(string sql, object? parameters = null)
        {
            using IDbConnection connection = CreateConnection();
            return await connection.ExecuteAsync(sql, parameters);
        }

        public async Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null)
        {
            using IDbConnection connection = CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<T>(sql, parameters);
        }

        public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null)
        {
            using IDbConnection connection = CreateConnection();
            return (await connection.QueryAsync<T>(sql, parameters)).AsList();
        }
    }
}