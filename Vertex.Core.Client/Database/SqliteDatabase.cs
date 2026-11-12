// Copyright 2026 Entex Interactive

using Vertex.Core.Client.Settings;
using Vertex.Core.Data;

namespace Vertex.Core.Client.Database
{
    public class SqliteDatabase
    {
        private readonly DapperContext _dapper;

        public SqliteDatabase()
        {
            string path = Path.Combine(ClientSettings.RoamingDirectory, "VertexClient.sqlite");
            _dapper = new DapperContext($"Data Source={path};Cache=Shared;Mode=ReadWriteCreate);");
        }
        
        public SqliteDatabase(string path)
        {
            _dapper = new DapperContext($"Data Source={path};Cache=Shared;Mode=ReadWriteCreate);");
        }

        public async Task InitializeAsync()
        {
            Log.Information("Creating index database...");
        }

        public async Task<IReadOnlyList<VFile>> GetFilesAsync(string path)
        {
            return [];
        }
    }
}