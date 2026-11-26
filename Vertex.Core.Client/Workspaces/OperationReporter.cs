// Copyright 2026 Entex Interactive

using Vertex.Core.Net;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// What a phase's counts measure.
    /// </summary>
    public enum PhaseUnit
    {
        /// <summary>Files.</summary>
        Files,

        /// <summary>Bytes.</summary>
        Bytes
    }

    /// <summary>
    /// One measurable part of a long operation, e.g. "Hashing" or "Syncing". The operation updates it from any thread;
    /// a host reads it to draw progress.
    /// </summary>
    public sealed class OperationPhase
    {
        private long _total;
        private long _done;
        private volatile bool _started;

        public OperationPhase(string name, PhaseUnit unit, bool started = true)
        {
            Name = name;
            Unit = unit;
            _started = started;
        }

        /// <summary>Shown to the user, e.g. "Submitting".</summary>
        public string Name { get; }

        public PhaseUnit Unit { get; }

        /// <summary>What the counts are, e.g. "120 file(s)", or null.</summary>
        public string? Detail { get; init; }

        /// <summary>
        /// The bytes moved by this phase's transfer, for byte counts and speed. A byte phase with a transfer takes its
        /// counts from it.
        /// </summary>
        public TransferProgress? Transfer { get; init; }

        /// <summary>False while the phase waits for an earlier one.</summary>
        public bool Started => _started;

        public long Total => Unit == PhaseUnit.Bytes && Transfer is not null ? Transfer.TotalBytes : Interlocked.Read(ref _total);

        public long Done => Unit == PhaseUnit.Bytes && Transfer is not null ? Transfer.DoneBytes : Interlocked.Read(ref _done);

        public void Start()
        {
            _started = true;
        }

        public void SetTotal(long total)
        {
            Interlocked.Exchange(ref _total, total);
        }

        public void Add(long done)
        {
            Interlocked.Add(ref _done, done);
        }

        /// <summary>Marks the phase finished, e.g. when the last file was skipped rather than counted.</summary>
        public void Complete()
        {
            Interlocked.Exchange(ref _done, Interlocked.Read(ref _total));
        }
    }

    /// <summary>
    /// How a host shows a workspace operation's progress: the CLI draws spinners and bars, the GUI pushes events.
    /// Operations never print.
    /// </summary>
    public interface IOperationReporter
    {
        /// <summary>
        /// Runs a step with no measurable progress, e.g. "Scanning..." or "Checking server...".
        /// </summary>
        Task<T> StepAsync<T>(string description, Func<Task<T>> work);

        /// <summary>
        /// Runs <paramref name="work"/> while showing <paramref name="phases"/>. Returns once the work finishes and
        /// rethrows its exception.
        /// </summary>
        Task ProgressAsync(IReadOnlyList<OperationPhase> phases, Func<Task> work);
    }

    /// <summary>
    /// Shows nothing.
    /// </summary>
    public sealed class SilentReporter : IOperationReporter
    {
        public static SilentReporter Instance { get; } = new();

        public Task<T> StepAsync<T>(string description, Func<Task<T>> work)
        {
            return work();
        }

        public Task ProgressAsync(IReadOnlyList<OperationPhase> phases, Func<Task> work)
        {
            return work();
        }
    }

    public static class OperationReporterExtensions
    {
        /// <summary>
        /// Scans the workspace as a step.
        /// </summary>
        public static Task<WorkspaceStatus> ScanAsync(this IOperationReporter reporter, string description, VWorkspace workspace, WorkspaceIndex index, ScanOptions options,
            CancellationToken ct)
        {
            return reporter.StepAsync(description, () => Task.Run(() => WorkspaceScanner.Scan(workspace, index, options, ct), ct));
        }
    }
}
