// Copyright 2026 Entex Interactive

using Vertex.Tests.Scenarios;

namespace Vertex.Tests
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            using CancellationTokenSource cancel = new();
            Console.CancelKeyPress += (_, e) =>
            {
                // First Ctrl+C stops the run and saves the report; a second one exits immediately.
                if (cancel.IsCancellationRequested) return;
                e.Cancel = true;
                AnsiConsole.MarkupLine("[yellow]Stopping... (Ctrl+C again to exit without a report)[/]");
                cancel.Cancel();
            };

            CommandApp app = new();
            app.Configure(config =>
            {
                config.SetApplicationName("vx-bench");
                config.AddExample("all", "--server", "https://vx.example.com", "--profile", "standard");
                config.AddExample("upload", "--profile", "stress", "--concurrency", "32");
                config.AddExample("soak", "--duration", "30m");

                config.AddCommand<AllCommand>("all").WithDescription("Runs every scenario except soak, in order.");
                config.AddCommand<ScenarioCommand<LatencyScenario>>("latency").WithDescription(new LatencyScenario().Description);
                config.AddCommand<ScenarioCommand<UploadScenario>>("upload").WithDescription(new UploadScenario().Description);
                config.AddCommand<ScenarioCommand<DedupScenario>>("dedup").WithDescription(new DedupScenario().Description);
                config.AddCommand<ScenarioCommand<DownloadScenario>>("download").WithDescription(new DownloadScenario().Description);
                config.AddCommand<ScenarioCommand<BulkSubmitScenario>>("submit").WithDescription(new BulkSubmitScenario().Description);
                config.AddCommand<ScenarioCommand<ConcurrentSubmitScenario>>("submitters").WithDescription(new ConcurrentSubmitScenario().Description);
                config.AddCommand<ScenarioCommand<LockScenario>>("locks").WithDescription(new LockScenario().Description);
                config.AddCommand<ScenarioCommand<EventFanoutScenario>>("events").WithDescription(new EventFanoutScenario().Description);
                config.AddCommand<ScenarioCommand<SoakScenario>>("soak").WithDescription(new SoakScenario().Description);
            });

            return await app.RunAsync(args, cancel.Token);
        }
    }

    public sealed class AllCommand : AsyncCommand<BenchSettings>
    {
        public override Task<int> ExecuteAsync(CommandContext context, BenchSettings settings, CancellationToken cancellationToken)
        {
            return BenchRunner.RunAsync(settings, ScenarioCatalog.All, cancellationToken);
        }
    }

    public sealed class ScenarioCommand<TScenario> : AsyncCommand<BenchSettings> where TScenario : IScenario, new()
    {
        public override Task<int> ExecuteAsync(CommandContext context, BenchSettings settings, CancellationToken cancellationToken)
        {
            return BenchRunner.RunAsync(settings, [new TScenario()], cancellationToken);
        }
    }
}
