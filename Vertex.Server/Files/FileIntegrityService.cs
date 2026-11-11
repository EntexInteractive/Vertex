// Copyright 2026 Entex Interactive

using Vertex.Server.Database;

namespace Vertex.Server.FileServices
{
    /// <summary>
    /// Represents a background service for integrity checking of files.
    /// </summary>
    public class FileIntegrityService : BackgroundService
    {
        private readonly ILogger<FileIntegrityService> _logger;
        private readonly CacheContext _cache;

        /// <inheritdoc />
        public FileIntegrityService(ILogger<FileIntegrityService> logger, CacheContext cache)
        {
            _logger = logger;
            _cache = cache;
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            TimeSpan interval = TimeSpan.FromHours(24);
            while (!stoppingToken.IsCancellationRequested)
            {
                // Only 1 instance can cache the key allowing syncing on only 1 machine in the cluster.
                if (await _cache.TrySetKeyAsync("file_integrity_check", interval))
                {
                    _logger.LogDebug("Starting file checks...");
                }

                _logger.LogDebug("File checks complete");
                await Task.Delay(interval, stoppingToken);
            }
        }
    }
}