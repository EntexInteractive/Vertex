// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Globalization;

namespace Vertex.Mirror
{
    /// <summary>
    /// Flags shared by pull and push: the Vertex side of the mirror and how long to run.
    /// </summary>
    public abstract class MirrorSettings : CommandSettings
    {
        [CommandOption("--server|-s")]
        [Description("The Vertex server. Defaults to the one set with 'vx config server', then http://127.0.0.1:5000.")]
        public string? Server { get; set; }

        [CommandOption("--token")]
        [Description("Access token to use instead of the 'vx login' one (also read from VX_MIRROR_TOKEN).")]
        public string? Token { get; set; }

        [CommandOption("--project|-p")]
        [Description("The Vertex project to mirror. Required.")]
        public string? Project { get; set; }

        [CommandOption("--branch|-b")]
        [Description("The Vertex branch to mirror. Defaults to main.")]
        [DefaultValue("main")]
        public string Branch { get; set; } = "main";

        [CommandOption("--interval|-i")]
        [Description("Time between mirror cycles, e.g. 30s, 5m or 1h.")]
        public string? Interval { get; set; }

        [CommandOption("--duration|-d")]
        [Description("Stop after this long, e.g. 9h. Without it, runs until Ctrl+C.")]
        public string? Duration { get; set; }

        [CommandOption("--work")]
        [Description("Folder for the working copy. Defaults to a new folder in the system temp folder, deleted at the end.")]
        public string? Work { get; set; }

        [CommandOption("--out|-o")]
        [Description("Folder for the log (and pull's per-cycle CSV and evidence). Defaults to ./mirror-results.")]
        [DefaultValue("mirror-results")]
        public string Out { get; set; } = "mirror-results";

        [CommandOption("--p4-port")]
        [Description("Perforce server (P4PORT), with --p4. Defaults to p4's own settings.")]
        public string? P4Port { get; set; }

        [CommandOption("--p4-user")]
        [Description("Perforce user (P4USER), with --p4. Defaults to p4's own settings.")]
        public string? P4User { get; set; }

        public TimeSpan? ParsedDuration => Duration is null ? null : ParseDuration(Duration);

        public TimeSpan IntervalOr(TimeSpan fallback)
        {
            return Interval is null ? fallback : ParseDuration(Interval)!.Value;
        }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Project))
                return ValidationResult.Error("Name the Vertex project with --project <name>.");

            if (Duration is not null && ParseDuration(Duration) is null)
                return ValidationResult.Error($"Can't read --duration '{Duration}'. Use a number with s, m or h, e.g. --duration 9h.");

            if (Interval is not null && ParseDuration(Interval) is null)
                return ValidationResult.Error($"Can't read --interval '{Interval}'. Use a number with s, m or h, e.g. --interval 5m.");

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

    public sealed class PullSettings : MirrorSettings
    {
        [CommandOption("--git|-g")]
        [Description("Pull from this git repository: a URL or a local path. Use either --git or --p4.")]
        public string? Git { get; set; }

        [CommandOption("--p4")]
        [Description("Pull from this Perforce depot path, e.g. //depot/game/... (vx-mirror makes its own client workspace). Use either --git or --p4.")]
        public string? P4Path { get; set; }

        [CommandOption("--start")]
        [Description("Where to start instead of the first commit or changelist (or where the last run stopped): for git a commit number (1 = the first) or a sha/tag, for Perforce a changelist number.")]
        public string? Start { get; set; }

        [CommandOption("--full-check-every")]
        [Description("Every N cycles, also compare the whole branch listing and re-download every file. Defaults to 100; it also runs when caught up.")]
        public int? FullCheckEvery { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Git) == string.IsNullOrWhiteSpace(P4Path))
                return ValidationResult.Error("Say where to pull from: --git <url or path> for a git repository, or --p4 //depot/path/... for Perforce.");

            if (P4Path is not null && (!P4Path.Trim().StartsWith("//", StringComparison.Ordinal) || P4Path.Contains('@') || P4Path.Contains('#')))
                return ValidationResult.Error($"--p4 '{P4Path}' isn't a depot path. Pass the folder to pull in depot syntax, e.g. --p4 //depot/game/...");

            if (FullCheckEvery is < 1)
                return ValidationResult.Error("--full-check-every must be at least 1.");

            return base.Validate();
        }
    }

    public sealed class PushSettings : MirrorSettings
    {
        [CommandOption("--git|-g")]
        [Description("Push to this git repository (a URL or a path to a bare repository). Use either --git or --p4.")]
        public string? Git { get; set; }

        [CommandOption("--git-branch")]
        [Description("The git branch to push to. Defaults to the Vertex branch's name.")]
        public string? GitBranch { get; set; }

        [CommandOption("--p4")]
        [Description("Submit to this Perforce client workspace. Its root and view decide where files go. Use either --git or --p4.")]
        public string? P4Client { get; set; }

        [CommandOption("--once")]
        [Description("Push every revision not mirrored yet, then exit instead of waiting for new ones.")]
        public bool Once { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Git) == string.IsNullOrWhiteSpace(P4Client))
                return ValidationResult.Error("Say where to push: --git <url or path> for a git repository, or --p4 <client workspace> for Perforce.");

            return base.Validate();
        }
    }
}
