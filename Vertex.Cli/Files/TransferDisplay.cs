// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Net;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Progress bars for uploads and downloads.
    /// </summary>
    public static class TransferDisplay
    {
        /// <summary>How often the byte counts and speed in a task's description are refreshed.</summary>
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

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
        /// Keeps <paramref name="task"/>'s description showing the bytes moved and the speed until <paramref name="work"/>
        /// finishes, then awaits it (so its exception surfaces).
        /// </summary>
        /// <param name="what">The task's name, e.g. "Downloading".</param>
        /// <param name="detail">What the bar counts, e.g. "120 file(s)".</param>
        public static async Task FollowAsync(Task work, ProgressTask task, TransferProgress transfer, string what, string detail)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!work.IsCompleted)
            {
                task.Description = Describe(what, detail, transfer, timer.Elapsed);
                await Task.WhenAny(work, Task.Delay(RefreshInterval));
            }

            task.Description = Describe(what, detail, transfer, timer.Elapsed);
            await work;
        }

        /// <summary>
        /// Keeps a byte-counted task's value and maximum in step with <paramref name="transfer"/> until
        /// <paramref name="work"/> finishes, with the bytes and speed in its description.
        /// </summary>
        public static async Task FollowBytesAsync(Task work, ProgressTask task, TransferProgress transfer, string what)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!work.IsCompleted)
            {
                Update(task, transfer, what, timer.Elapsed);
                await Task.WhenAny(work, Task.Delay(RefreshInterval));
            }

            await work;
            Update(task, transfer, what, timer.Elapsed);
            task.Value = task.MaxValue;
        }

        private static void Update(ProgressTask task, TransferProgress transfer, string what, TimeSpan elapsed)
        {
            long total = transfer.TotalBytes;
            task.MaxValue = Math.Max(1, total);
            task.Value = Math.Min(transfer.DoneBytes, task.MaxValue);
            long done = transfer.DoneBytes;
            task.Description = $"{what} [grey]{Bytes(done)} of {Bytes(total)}{Speed(done, elapsed)}[/]";
        }

        private static string Describe(string what, string detail, TransferProgress transfer, TimeSpan elapsed)
        {
            long done = transfer.DoneBytes;
            return $"{what} {Markup.Escape(detail)} [grey]({Bytes(done)}{Speed(done, elapsed)})[/]";
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
