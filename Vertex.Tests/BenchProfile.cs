// Copyright 2026 Entex Interactive

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Vertex.Tests
{
    /// <summary>
    /// How much load a run puts on the server. Every value can be overridden with a flag.
    /// </summary>
    /// <param name="TransferConcurrency">The stream counts the upload and download scenarios compare.</param>
    /// <param name="SubmitThink">Pause between one simulated developer's submits.</param>
    /// <param name="LockThink">Pause between one simulated workspace's lock and unlock cycles.</param>
    public sealed record BenchProfile(
        string Name,
        int LargeFiles,
        long LargeFileSize,
        int SmallFiles,
        int Clients,
        int Subscribers,
        TimeSpan Duration,
        int[] TransferConcurrency,
        ThinkTime SubmitThink,
        ThinkTime LockThink)
    {
        private const long Megabyte = 1024 * 1024;

        // smoke, standard and stress run flat out (no think time): ceiling tests, comparable with earlier runs.

        public static readonly BenchProfile Smoke = new("smoke", 4, 64 * Megabyte, 2_000, 4, 4, TimeSpan.FromSeconds(15), [1, 4], ThinkTime.None, ThinkTime.None);

        public static readonly BenchProfile Standard = new("standard", 8, 256 * Megabyte, 100_000, 16, 50, TimeSpan.FromSeconds(60), [1, 4, 8, 16], ThinkTime.None, ThinkTime.None);

        public static readonly BenchProfile Stress = new("stress", 20, 512 * Megabyte, 250_000, 64, 200, TimeSpan.FromMinutes(5), [1, 4, 8, 16, 32], ThinkTime.None, ThinkTime.None);

        /// <summary>
        /// What a real team feels: everyone has vx open (one event stream each), developers submit every 30-60 s and
        /// lock files as they save every 10-30 s. Small data sets, so it's about latency under realistic load.
        /// </summary>
        public static readonly BenchProfile Team = new("team", 4, 64 * Megabyte, 2_000, 100, 100, TimeSpan.FromMinutes(10), [1, 4],
            new ThinkTime(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)), new ThinkTime(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));

        public long LargeTotalBytes => LargeFiles * LargeFileSize;

        public int MaximumConcurrency => TransferConcurrency.Max();

        public static bool TryGet(string name, [NotNullWhen(true)] out BenchProfile? profile)
        {
            profile = name.Trim().ToLowerInvariant() switch
            {
                "smoke" => Smoke,
                "standard" => Standard,
                "stress" => Stress,
                "team" => Team,
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
                TransferConcurrency = settings.Concurrency is int streams ? [streams] : profile.TransferConcurrency,
                SubmitThink = settings.Think is string think ? ThinkTime.Parse(think)! : profile.SubmitThink,
                LockThink = settings.LockThink is string lockThink ? ThinkTime.Parse(lockThink)! : profile.LockThink
            };
        }

        public override string ToString()
        {
            return $"{Name}: {LargeFiles} x {Format.Bytes(LargeFileSize)} assets, {SmallFiles:N0} small files, {Clients} clients, " +
                   $"{Subscribers} subscribers, {Format.Duration(Duration)} timed scenarios, streams {string.Join("/", TransferConcurrency)}, " +
                   $"think {SubmitThink} between submits, {LockThink} between locks";
        }
    }

    /// <summary>
    /// A random pause between <see cref="Min"/> and <see cref="Max"/>, like a person working between actions.
    /// </summary>
    public sealed record ThinkTime(TimeSpan Min, TimeSpan Max)
    {
        public static readonly ThinkTime None = new(TimeSpan.Zero, TimeSpan.Zero);

        public bool IsNone => Max <= TimeSpan.Zero;

        /// <summary>
        /// A pause in the range, drawn from the caller's seeded random so runs repeat.
        /// </summary>
        public TimeSpan Next(Random random)
        {
            return IsNone ? TimeSpan.Zero : Min + (Max - Min) * random.NextDouble();
        }

        /// <summary>
        /// A random start in [0, Max], so simulated people don't all act in the same second.
        /// </summary>
        public TimeSpan Stagger(Random random)
        {
            return IsNone ? TimeSpan.Zero : Max * random.NextDouble();
        }

        /// <summary>
        /// Reads seconds: "30-60" for a range, "45" for a fixed pause, "0" for none.
        /// </summary>
        /// <returns>Null when the text can't be read.</returns>
        public static ThinkTime? Parse(string text)
        {
            string[] parts = text.Trim().TrimEnd('s').Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2) return null;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double min) || min < 0) return null;
            double max = min;
            if (parts.Length == 2 && (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out max) || max < min)) return null;
            return new ThinkTime(TimeSpan.FromSeconds(min), TimeSpan.FromSeconds(max));
        }

        public override string ToString()
        {
            if (IsNone) return "none";
            return Min == Max ? $"{Max.TotalSeconds:0.#} s" : $"{Min.TotalSeconds:0.#}-{Max.TotalSeconds:0.#} s";
        }
    }
}
