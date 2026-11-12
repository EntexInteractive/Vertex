// Copyright 2026 Entex Interactive

namespace Vertex.Core.Client.Database
{
    public class SqliteDatabase
    {
        private readonly DapperContext _dapper;

        public SqliteDatabase(string path)
        {
            _dapper = new DapperContext($"Data Source={path};Cache=Shared;Mode=ReadWriteCreate);");
        }

        public async Task InitializeAsync()
        {
            Log.Information("Creating index database...");
        }
    }
}