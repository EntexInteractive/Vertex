// Copyright 2026 Entex Interactive

using Vertex.Core.Client.Workspaces;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Progress bars for uploads and downloads.
    /// </summary>
    public static class TransferDisplay
    {
        /// <summary>
        /// A progress display whose tasks count files: description (with bytes and speed), bar, percentage and time
        /// left. Byte-counted tasks also work, since the bytes are in the description.
        /// </summary>
        public static Progress Progress()
        {
            return AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn(), new SpinnerColumn());
        }

        /// <summary>
        /// A phase's bar label: its name, then for a transfer the bytes moved and the speed, e.g.
        /// "Syncing 120 file(s) (3.4 MB, 1.1 MB/s)" or "Submitting 210 MB of 344 MB, 40 MB/s".
        /// </summary>
        public static string Describe(OperationPhase phase, TimeSpan elapsed)
        {
            string name = Markup.Escape(phase.Name);
            if (phase.Transfer is null) return name;

            long done = phase.Transfer.DoneBytes;
            return phase.Unit == PhaseUnit.Bytes
                ? $"{name} [grey]{Bytes(done)} of {Bytes(phase.Transfer.TotalBytes)}{Speed(done, elapsed)}[/]"
                : $"{name} {Markup.Escape(phase.Detail ?? "")} [grey]({Bytes(done)}{Speed(done, elapsed)})[/]";
        }

        private static string Speed(long done, TimeSpan elapsed)
        {
            return elapsed.TotalSeconds >= 0.5 ? $", {Bytes((long)(done / elapsed.TotalSeconds))}/s" : "";
        }

        /// <summary>
        /// A byte count for people: "512 B", "3.4 MB", "1.2 GB".
        /// </summary>
        public static string Bytes(long bytes)
        {
            return bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
                < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
                _ => $"{bytes / (1024.0 * 1024 * 1024):N2} GB"
            };
        }
    }
}
