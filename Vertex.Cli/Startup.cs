// Copyright 2026 Entex Interactive

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vertex.Core.Contexts;
using Vertex.Core.Settings;

namespace Vertex.Cli
{
    public class Startup
    {
        public static ServiceProvider CreateServiceProvider()
        {
            ServiceCollection services = new();
            services.AddDbContext<ClientDbContext>(options =>
            {
                string dbPath = Path.Combine(ClientSettings.RoamingDirectory, "vertex.sqlite");
                options.UseSqlite($"Data Source={dbPath}");
            });

            return services.BuildServiceProvider();
        }

        public static async Task InitializeDatabaseAsync(IServiceProvider serviceProvider)
        {
            await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

            ClientDbContext db = scope.ServiceProvider.GetRequiredService<ClientDbContext>();
            await db.Database.MigrateAsync();
        }
    }
}