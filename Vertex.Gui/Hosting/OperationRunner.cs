// Copyright 2026 Entex Interactive

using Vertex.Core.Client.Workspaces;

namespace Vertex.Gui.Hosting
{
    /// <summary>
    /// One phase of a running operation, e.g. "Submitting" 210 MB of 344 MB.
    /// </summary>
    /// <param name="Unit"><c>Files</c> or <c>Bytes</c>.</param>
    /// <param name="TransferredBytes">Bytes moved so far, for phases that transfer content (else 0).</param>
    public sealed record PhaseDto(string Name, PhaseUnit Unit, long Done, long Total, bool Started, long TransferredBytes);

    /// <summary>
    /// A running submit, sync, revert, checkout or clone.
    /// </summary>
    /// <param name="Kind"><c>submit</c>, <c>sync</c>, <c>revert</c>, <c>checkout</c> or <c>clone</c>.</param>
    /// <param name="Title">What it does, e.g. "Submitting 12 files".</param>
    /// <param name="Step">The current step without measurable progress, e.g. "Scanning...", or null.</param>
    /// <param name="StartedAt">Unix milliseconds.</param>
    public sealed record OperationDto(Guid Id, string Kind, string Title, string? Step, IReadOnlyList<PhaseDto> Phases, long StartedAt);

    /// <summary>
    /// How an operation ended.
    /// </summary>
    /// <param name="Ok">False when it failed, was refused or was canceled.</param>
    /// <param name="Summary">One line for the user, e.g. "Submitted #12 (3 files)." or why it didn't happen.</param>
    /// <param name="Warnings">Things to know that didn't stop it, e.g. files skipped.</param>
    /// <param name="Conflicts">Files changed both here and on the server that a sync left alone.</param>
    /// <param name="Revision">The revision submitted or synced to, or null.</param>
    public sealed record OperationResultDto(bool Ok, string Summary, IReadOnlyList<string> Warnings, IReadOnlyList<string> Conflicts, long? Revision)
    {
        public static OperationResultDto Success(string summary, long? revision = null, IReadOnlyList<string>? warnings = null, IReadOnlyList<string>? conflicts = null)
        {
            return new OperationResultDto(true, summary, warnings ?? [], conflicts ?? [], revision);
        }

        public static OperationResultDto Refused(string summary, IReadOnlyList<string>? warnings = null)
        {
            return new OperationResultDto(false, summary, warnings ?? [], [], null);
        }
    }

    /// <param name="Kind">As in <see cref="OperationDto.Kind"/>.</param>
    public sealed record OperationDoneDto(Guid Id, string Kind, OperationResultDto Result);

    /// <summary>
    /// Runs one long workspace operation at a time in the background, with the workspace monitor paused, and pushes
    /// its progress to the UI: <c>operation.progress</c> (an <see cref="OperationDto"/>, at most every 250 ms) and
    /// <c>operation.done</c> (an <see cref="OperationDoneDto"/>).
    /// </summary>
    public sealed class OperationRunner(WorkspaceRegistry registry, HostEvents events, ServerSession session, ILogger<OperationRunner> logger)
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

        private readonly Lock _lock = new();
        private Running? _running;

        /// <summary>
        /// The running operation, or null.
        /// </summary>
        public OperationDto? Current
        {
            get
            {
                lock (_lock)
                {
                    return _running?.Reporter.Snapshot();
                }
            }
        }

        /// <summary>
        /// Starts <paramref name="work"/> and returns at once.
        /// </summary>
        /// <exception cref="HostProblemException">Another operation is running.</exception>
        public OperationDto Start(string kind, string title, Func<LinkedWorkspace, IOperationReporter, CancellationToken, Task<OperationResultDto>> work)
        {
            Running running;
            lock (_lock)
            {
                if (_running is not null)
                {
                    string what = _running.Reporter.Title.ToLowerInvariant();
                    throw new HostProblemException($"Already {what}. Wait for it to finish, or cancel it.", StatusCodes.Status409Conflict);
                }

                running = new Running(new HostReporter(Guid.NewGuid(), kind, title, events), new CancellationTokenSource());
                _running = running;
            }

            _ = Task.Run(() => RunAsync(running, work));
            return running.Reporter.Snapshot();
        }

        /// <summary>
        /// Cancels the operation when it's the running one. Work already done is kept, the same as Ctrl+C in vx.
        /// </summary>
        public bool Cancel(Guid id)
        {
            lock (_lock)
            {
                if (_running is null || _running.Reporter.Id != id) return false;
                _running.Cancel.Cancel();
                return true;
            }
        }

        private async Task RunAsync(Running running, Func<LinkedWorkspace, IOperationReporter, CancellationToken, Task<OperationResultDto>> work)
        {
            HostReporter reporter = running.Reporter;
            CancellationToken ct = running.Cancel.Token;
            using Timer timer = new(_ => reporter.Publish(), null, TimeSpan.Zero, RefreshInterval);
            OperationResultDto result;
            try
            {
                result = await registry.ExclusiveAsync(linked => work(linked, reporter, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = OperationResultDto.Refused("Canceled. What finished is kept; run it again to do the rest.");
            }
            catch (CheckoutIncompleteException ex) when (ex.InnerException is OperationCanceledException && ct.IsCancellationRequested)
            {
                result = OperationResultDto.Refused($"Canceled. The workspace is on '{ex.Branch}' now; sync to finish updating its files.");
            }
            catch (Exception ex)
            {
                (int status, string detail) = ServerErrorHandler.Describe(ex, session);
                if (status >= 500) logger.LogError(ex, "{Kind} failed", reporter.Kind);
                result = OperationResultDto.Refused(detail);
            }

            lock (_lock)
            {
                _running = null;
            }

            running.Cancel.Dispose();
            events.Publish("operation.done", new OperationDoneDto(reporter.Id, reporter.Kind, result));
            registry.PublishChanged();
        }

        private sealed record Running(HostReporter Reporter, CancellationTokenSource Cancel);

        /// <summary>
        /// Records an operation's steps and phases for <see cref="OperationRunner"/> to publish.
        /// </summary>
        private sealed class HostReporter(Guid id, string kind, string title, HostEvents events) : IOperationReporter
        {
            private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            private volatile string? _step;
            private volatile IReadOnlyList<OperationPhase> _phases = [];

            public Guid Id { get; } = id;

            public string Kind { get; } = kind;

            public string Title { get; } = title;

            public async Task<T> StepAsync<T>(string description, Func<Task<T>> work)
            {
                _step = description;
                Publish();
                try
                {
                    return await work();
                }
                finally
                {
                    _step = null;
                }
            }

            public async Task ProgressAsync(IReadOnlyList<OperationPhase> phases, Func<Task> work)
            {
                _phases = phases;
                Publish();
                try
                {
                    await work();
                }
                finally
                {
                    foreach (OperationPhase phase in phases) phase.Complete();
                    Publish();
                    _phases = [];
                }
            }

            public OperationDto Snapshot()
            {
                List<PhaseDto> phases = _phases.Select(p => new PhaseDto(p.Name, p.Unit, p.Done, p.Total, p.Started, p.Transfer?.DoneBytes ?? 0)).ToList();
                return new OperationDto(Id, Kind, Title, _step, phases, _startedAt);
            }

            public void Publish()
            {
                events.Publish("operation.progress", Snapshot());
            }
        }
    }
}
