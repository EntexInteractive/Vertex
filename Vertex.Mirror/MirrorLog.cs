// Copyright 2026 Entex Interactive

namespace Vertex.Mirror
{
    /// <summary>
    /// The run's log file (<c>&lt;command&gt;-&lt;run&gt;.log</c> under --out): every problem and notable event,
    /// timestamped in UTC, so a run left going all day can be checked afterwards.
    /// </summary>
    public sealed class MirrorLog
    {
        private readonly object _gate = new();
        private long _problems;

        public MirrorLog(string outFolder, string command, string runId)
        {
            Directory.CreateDirectory(outFolder);
            OutFolder = Path.GetFullPath(outFolder);
            RunId = runId;
            FilePath = Path.Combine(OutFolder, $"{command}-{runId}.log");
        }

        public string OutFolder { get; }

        public string RunId { get; }

        public string FilePath { get; }

        public long Problems => Interlocked.Read(ref _problems);

        /// <summary>
        /// Writes a line to the log only.
        /// </summary>
        public void Write(string message)
        {
            lock (_gate) File.AppendAllText(FilePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC {message}{Environment.NewLine}");
        }

        /// <summary>
        /// Writes a line to the log and the console.
        /// </summary>
        public void Info(string message)
        {
            Write(message);
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
        }

        /// <summary>
        /// Counts and records a problem: a red console line and a PROBLEM line in the log.
        /// </summary>
        public void Problem(string message, string? detail = null)
        {
            Interlocked.Increment(ref _problems);
            Write("PROBLEM " + message + (detail is null ? "" : " " + detail));
            AnsiConsole.MarkupLine($"[red]  ! {Markup.Escape(message)}[/]");
        }
    }

    public static class Format
    {
        public static string Bytes(double bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            int unit = 0;
            while (Math.Abs(bytes) >= 1024 && unit < units.Length - 1)
            {
                bytes /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes:N0} B" : $"{bytes:N1} {units[unit]}";
        }

        public static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 1) return $"{span.TotalMilliseconds:N0} ms";
            if (span.TotalMinutes < 1) return $"{span.TotalSeconds:N2} s";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        public static string Milliseconds(TimeSpan elapsed)
        {
            return $"{elapsed.TotalMilliseconds,5:N0} ms";
        }

        public static string ShortSha(string commit)
        {
            return commit.Length > 10 ? commit[..10] : commit;
        }

        /// <summary>
        /// The first line of a message, cut to <paramref name="length"/> characters, for one-line output.
        /// </summary>
        public static string FirstLine(string message, int length = 60)
        {
            string line = message.Split('\n', 2)[0].Trim();
            return line.Length > length ? line[..(length - 3)] + "..." : line;
        }

        public static string[] Lines(string text)
        {
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    /// <summary>
    /// Timings of one phase over a run, for the summary at the end.
    /// </summary>
    public sealed class PhaseStats(string name)
    {
        private readonly List<double> _milliseconds = new();

        public string Name { get; } = name;

        public int Errors { get; private set; }

        public void Record(TimeSpan elapsed)
        {
            lock (_milliseconds) _milliseconds.Add(elapsed.TotalMilliseconds);
        }

        public void Error()
        {
            lock (_milliseconds) Errors++;
        }

        /// <summary>
        /// Count, median, 95th percentile and maximum, in milliseconds.
        /// </summary>
        public (int Count, double P50, double P95, double Max) Summarize()
        {
            double[] sorted;
            lock (_milliseconds) sorted = _milliseconds.Order().ToArray();
            if (sorted.Length == 0) return (0, 0, 0, 0);
            return (sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.95), sorted[^1]);
        }

        private static double Percentile(double[] sorted, double p)
        {
            int index = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }

        /// <summary>
        /// A table of phases, printed at the end of a run.
        /// </summary>
        public static void Print(IEnumerable<PhaseStats> phases)
        {
            Table table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Phase").AddColumn(new TableColumn("Count").RightAligned()).AddColumn(new TableColumn("p50 ms").RightAligned())
                .AddColumn(new TableColumn("p95 ms").RightAligned()).AddColumn(new TableColumn("max ms").RightAligned()).AddColumn(new TableColumn("Errors").RightAligned());
            foreach (PhaseStats phase in phases)
            {
                (int count, double p50, double p95, double max) = phase.Summarize();
                if (count == 0 && phase.Errors == 0) continue;
                table.AddRow(Markup.Escape(phase.Name), count.ToString("N0"), p50.ToString("N0"), p95.ToString("N0"), max.ToString("N0"),
                    phase.Errors == 0 ? "0" : $"[red]{phase.Errors:N0}[/]");
            }

            AnsiConsole.Write(table);
        }
    }
}
