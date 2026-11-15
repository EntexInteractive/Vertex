// Copyright 2026 Entex Interactive

using Vertex.Tests.Metrics;

namespace Vertex.Tests.Scenarios
{
    /// <summary>
    /// One benchmark. It records what it measured into the result and fails it with <see cref="ScenarioResult.Fail"/>
    /// when the server gave wrong answers; slow is reported, not failed.
    /// </summary>
    public interface IScenario
    {
        /// <summary>
        /// The command name, e.g. "upload".
        /// </summary>
        string Name { get; }

        string Description { get; }

        Task RunAsync(BenchContext context, ScenarioResult result, CancellationToken ct);
    }

    public static class ScenarioCatalog
    {
        /// <summary>
        /// What <c>all</c> runs, in order: cheap baselines first, then transfers (later ones reuse the uploaded data),
        /// then history, locks and events.
        /// </summary>
        public static IReadOnlyList<IScenario> All { get; } =
        [
            new LatencyScenario(),
            new UploadScenario(),
            new DedupScenario(),
            new DownloadScenario(),
            new BulkSubmitScenario(),
            new ConcurrentSubmitScenario(),
            new LockScenario(),
            new EventFanoutScenario()
        ];
    }
}
