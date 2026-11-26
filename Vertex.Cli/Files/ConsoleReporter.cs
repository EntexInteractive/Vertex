// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Vertex.Core.Client.Workspaces;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Shows a workspace operation's progress in the console: a spinner for each step, and progress bars for phases.
    /// </summary>
    public sealed class ConsoleReporter : IOperationReporter
    {
        /// <summary>How often the bars are refreshed.</summary>
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

        public static ConsoleReporter Instance { get; } = new();

        public async Task<T> StepAsync<T>(string description, Func<Task<T>> work)
        {
            T result = default!;
            await AnsiConsole.Status().StartAsync(description, async _ => { result = await work(); });
            return result;
        }

        public Task ProgressAsync(IReadOnlyList<OperationPhase> phases, Func<Task> work)
        {
            return TransferDisplay.Progress().StartAsync(async progress =>
            {
                List<(OperationPhase Phase, ProgressTask Task, Stopwatch Timer)> bars = phases
                    .Select(p => (p, progress.AddTask(p.Name, autoStart: p.Started, maxValue: Math.Max(1, p.Total)), new Stopwatch()))
                    .ToList();

                Task running = work();
                while (!running.IsCompleted)
                {
                    foreach ((OperationPhase phase, ProgressTask task, Stopwatch timer) in bars) Update(phase, task, timer);
                    await Task.WhenAny(running, Task.Delay(RefreshInterval));
                }

                await running;
                foreach ((OperationPhase phase, ProgressTask task, Stopwatch timer) in bars)
                {
                    Update(phase, task, timer);
                    task.Value = task.MaxValue;
                }
            });
        }

        private static void Update(OperationPhase phase, ProgressTask task, Stopwatch timer)
        {
            if (!phase.Started) return;
            if (!task.IsStarted) task.StartTask();
            if (!timer.IsRunning) timer.Start();

            task.MaxValue = Math.Max(1, phase.Total);
            task.Value = Math.Min(phase.Done, task.MaxValue);
            task.Description = TransferDisplay.Describe(phase, timer.Elapsed);
        }
    }
}
