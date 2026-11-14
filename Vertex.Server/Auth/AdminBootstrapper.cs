// Copyright 2026 Entex Interactive

using Vertex.Server.Users;

namespace Vertex.Server.Auth
{
    /// <summary>
    /// Creates the first server admin on a fresh server, so there's someone to add everyone else. Runs once at
    /// startup and does nothing when any user exists.
    /// </summary>
    public sealed class AdminBootstrapper : IHostedService
    {
        public const string AdminUsername = "admin";

        private readonly IServiceScopeFactory _scopes;
        private readonly ServerSettings _settings;
        private readonly ILogger<AdminBootstrapper> _logger;

        public AdminBootstrapper(IServiceScopeFactory scopes, ServerSettings settings, ILogger<AdminBootstrapper> logger)
        {
            _scopes = scopes;
            _settings = settings;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using IServiceScope scope = _scopes.CreateScope();
            UsersRepository users = scope.ServiceProvider.GetRequiredService<UsersRepository>();

            // DatabaseMigrator has created the auth tables before any hosted service starts.
            if (await users.AnyUsersAsync(cancellationToken)) return;

            if (_settings.AdminPassword is null)
            {
                _logger.LogWarning("No users exist, so nobody can log in. Set {Variable} and restart to create the '{Admin}' account.", ServerSettings.AdminPasswordVariable, AdminUsername);
                return;
            }

            if (await users.CreateFirstAdminAsync(AdminUsername, PasswordHasher.Hash(_settings.AdminPassword), cancellationToken))
            {
                _logger.LogInformation("Created the server admin '{Admin}'. Log in with 'vx login -u {Admin}'.", AdminUsername, AdminUsername);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
