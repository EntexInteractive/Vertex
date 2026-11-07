// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Vertex.Core.Data;

namespace Vertex.Core.Contexts
{
    public class ClientDbContext : DbContext
    {
        public ClientDbContext(DbContextOptions<ClientDbContext> options) : base(options) { }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            base.ConfigureConventions(configurationBuilder);
            configurationBuilder.UseUnixTime();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<VBranch>().HasKey(x => x.Id);
            modelBuilder.Entity<VFile>().HasKey(x => x.Id);
            modelBuilder.Entity<VProject>().HasKey(x => x.Id);
            modelBuilder.Entity<VUser>().HasKey(x => x.Id);
        }

        public DbSet<VFile> Files => Set<VFile>();
    }
}