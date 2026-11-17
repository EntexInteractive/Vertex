// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Globalization;

namespace Vertex.Tests
{
    /// <summary>
    /// Flags shared by every benchmark command. Anything left out comes from the chosen <see cref="BenchProfile"/>.
    /// </summary>
    public sealed class BenchSettings : CommandSettings
    {
        [CommandOption("--server|-s")]
        [Description("The server to benchmark. Defaults to the one set with 'vx config server', then http://127.0.0.1:5000.")]
        public string? Server { get; set; }

        [CommandOption("--token")]
        [Description("Access token to use instead of the 'vx login' one (also read from VX_BENCH_TOKEN).")]
        public string? Token { get; set; }

        [CommandOption("--project")]
        [Description("Project to run in, created when missing. Defaults to a new bench-<time> project per run.")]
        public string? Project { get; set; }

        [CommandOption("--profile|-p")]
        [Description("Load profile: smoke (about a minute), standard, stress (flat-out ceiling tests) or team (realistic pacing: " +
                     "100 clients, submits every 30-60 s, locks every 10-30 s). Defaults to smoke.")]
        [DefaultValue("smoke")]
        public string Profile { get; set; } = "smoke";

        [CommandOption("--clients|-c")]
        [Description("Concurrent simulated clients for the submit, lock and latency scenarios.")]
        public int? Clients { get; set; }

        [CommandOption("--concurrency")]
        [Description("Transfer streams per upload/download. Tests just this level instead of the profile's list.")]
        public int? Concurrency { get; set; }

        [CommandOption("--subscribers")]
        [Description("Event stream subscribers for the events and soak scenarios.")]
        public int? Subscribers { get; set; }

        [CommandOption("--duration|-d")]
        [Description("How long timed scenarios run, e.g. 90s, 5m, 1h. Soak runs until Ctrl+C without it.")]
        public string? Duration { get; set; }

        [CommandOption("--think")]
        [Description("Seconds each simulated developer waits between submits, e.g. 30-60, 45, or 0 for none. Overrides the profile.")]
        public string? Think { get; set; }

        [CommandOption("--lock-think")]
        [Description("Seconds each simulated workspace waits between lock cycles, e.g. 10-30, or 0 for none. Overrides the profile.")]
        public string? LockThink { get; set; }

        [CommandOption("--large-files")]
        [Description("Number of large assets to generate for the transfer scenarios.")]
        public int? LargeFiles { get; set; }

        [CommandOption("--large-size")]
        [Description("Size of each large asset in MB.")]
        public int? LargeSizeMb { get; set; }

        [CommandOption("--small-files")]
        [Description("Number of small files in the bulk submit tree.")]
        public int? SmallFiles { get; set; }

        [CommandOption("--out|-o")]
        [Description("Folder for the JSON and Markdown reports. Defaults to ./bench-results.")]
        [DefaultValue("bench-results")]
        public string Out { get; set; } = "bench-results";

        [CommandOption("--data")]
        [Description("Folder for generated test data. Defaults to the system temp folder.")]
        public string? Data { get; set; }

        [CommandOption("--keep-data")]
        [Description("Keeps the generated test data after the run.")]
        public bool KeepData { get; set; }

        /// <summary>
        /// The parsed <see cref="Duration"/>, or null when not given.
        /// </summary>
        public TimeSpan? ParsedDuration => Duration is null ? null : ParseDuration(Duration);

        public override ValidationResult Validate()
        {
            if (!BenchProfile.TryGet(Profile, out _))
                return ValidationResult.Error($"Unknown profile '{Profile}'. Use smoke, standard, stress, busy or team.");

            if (Duration is not null && ParseDuration(Duration) is null)
                return ValidationResult.Error($"Can't read duration '{Duration}'. Use a number with s, m or h, e.g. --duration 5m.");

            if (Think is not null && ThinkTime.Parse(Think) is null)
                return ValidationResult.Error($"Can't read --think '{Think}'. Use seconds, e.g. --think 30-60, --think 45 or --think 0.");

            if (LockThink is not null && ThinkTime.Parse(LockThink) is null)
                return ValidationResult.Error($"Can't read --lock-think '{LockThink}'. Use seconds, e.g. --lock-think 10-30.");

            if (Clients is < 1 || Concurrency is < 1 || Subscribers is < 0 || LargeFiles is < 1 || LargeSizeMb is < 1 || SmallFiles is < 1)
                return ValidationResult.Error("Counts and sizes must be positive.");

            return ValidationResult.Success();
        }

        /// <summary>
        /// Reads "90", "90s", "5m", "1.5h" or a TimeSpan ("00:05:00").
        /// </summary>
        public static TimeSpan? ParseDuration(string text)
        {
            text = text.Trim().ToLowerInvariant();
            if (text.Length == 0) return null;
            if (text.Contains(':')) return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan span) && span > TimeSpan.Zero ? span : null;

            double multiplier = text[^1] switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3600,
                _ => 0
            };

            string number = multiplier == 0 ? text : text[..^1];
            if (multiplier == 0) multiplier = 1;
            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0
                ? TimeSpan.FromSeconds(value * multiplier)
                : null;
        }
    }
}
