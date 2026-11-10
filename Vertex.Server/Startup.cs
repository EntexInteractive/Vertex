// Copyright 2026 Entex Interactive

using Npgsql;

namespace Vertex.Server
{
    public class Startup
    {
        public async Task InitializeDatabaseAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
        {
            string sql = await File.ReadAllTextAsync("Database/schema.sql", cancellationToken);
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}