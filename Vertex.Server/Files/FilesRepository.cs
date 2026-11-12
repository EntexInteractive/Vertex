// Copyright 2026 Entex Interactive

using Vertex.Server.Database;

namespace Vertex.Server.FileServices
{
    public class FilesRepository
    {
        private readonly PostgresDatabase _database;
        private readonly DapperContext _dapper;

        public FilesRepository(PostgresDatabase database, DapperContext dapper)
        {
            _database = database;
            _dapper = dapper;
        }
    }
}