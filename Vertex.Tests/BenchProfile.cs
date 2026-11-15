// Copyright 2026 Entex Interactive

using System.Diagnostics.CodeAnalysis;

namespace Vertex.Tests
{
    /// <summary>
    /// How much load a run puts on the server. Every value can be overridden with a flag.
    /// </summary>
    /// <param name="TransferConcurrency">The stream counts the upload and download scenarios compare.</param>
    public sealed record BenchProfile(
        string Name,
        int LargeFiles,
        long LargeFileSize,
        int SmallFiles,
        int Clients,
        int Subscribers,
        TimeSpan Duration,
        int[] TransferConcurrency)
    {
        private const long Megabyte = 1024 * 1024;

        public static readonly BenchProfile Smoke = new("smoke", 4, 64 * Megabyte, 2_000, 4, 4, TimeSpan.FromSeconds(15), [1, 4]);

        public static readonly BenchProfile Standard = new("standard", 8, 256 * Megabyte, 100_000, 16, 50, TimeSpan.FromSeconds(60), [1, 4, 8, 16]);

        public static readonly BenchProfile Stress = new("stress", 20, 512 * Megabyte, 250_000, 64, 200, TimeSpan.FromMinutes(5), [1, 4, 8, 16, 32]);

        public long LargeTotalBytes => LargeFiles * LargeFileSize;

        public int MaximumConcurrency => TransferConcurrency.Max();

        public static bool TryGet(string name, [NotNullWhen(true)] out BenchProfile? profile)
        {
            profile = name.Trim().ToLowerInvariant() switch
            {
                "smoke" => Smoke,
                "standard" => Standard,
                "stress" => Stress,
                _ => null
            };

            return profile is not null;
        }

        /// <summary>
        /// The named profile with the command line's overrides applied.
        /// </summary>
        public static BenchProfile Resolve(BenchSettings settings)
        {
            if (!TryGet(settings.Profile, out BenchProfile? profile)) throw new ArgumentException($"Unknown profile '{settings.Profile}'.");

            return profile with
            {
                LargeFiles = settings.LargeFiles ?? profile.LargeFiles,
                LargeFileSize = settings.LargeSizeMb is int mb ? mb * Megabyte : profile.LargeFileSize,
                SmallFiles = settings.SmallFiles ?? profile.SmallFiles,
                Clients = settings.Clients ?? profile.Clients,
                Subscribers = settings.Subscribers ?? profile.Subscribers,
                Duration = settings.ParsedDuration ?? profile.Duration,
                TransferConcurrency = settings.Concurrency is int streams ? [streams] : profile.TransferConcurrency
            };
        }

        public override string ToString()
        {
            return $"{Name}: {LargeFiles} x {Format.Bytes(LargeFileSize)} assets, {SmallFiles:N0} small files, {Clients} clients, " +
                   $"{Subscribers} subscribers, {Format.Duration(Duration)} timed scenarios, streams {string.Join("/", TransferConcurrency)}";
        }
    }
}
