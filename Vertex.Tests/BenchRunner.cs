// Copyright 2026 Entex Interactive

using Vertex.Tests.Metrics;
using Vertex.Tests.Scenarios;

namespace Vertex.Tests
{
    /// <summary>
    /// Runs scenarios one after another against one context, then prints and saves the report. Ctrl+C stops the
    /// current scenario and still saves what was measured.
    /// </summary>
    public static class BenchRunner
    {
        public static async Task<int> RunAsync(BenchSettings settings, IReadOnlyList<IScenario> scenarios, CancellationToken ct)
        {
            BenchContext context;
            try
            {
                context = await BenchContext.CreateAsync(settings, ct);
            }
            catch (BenchSetupException ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return 2;
            }

            using (context)
            {
                Report.Header(context);
                List<ScenarioResult> results = new();
                foreach (IScenario scenario in scenarios)
                {
                    if (ct.IsCancellationRequested) break;
                    ScenarioResult result = new(scenario.Name, scenario.Description);
                    results.Add(result);
                    AnsiConsole.MarkupLine($"[grey]{DateTime.UtcNow:HH:mm:ss} UTC[/] running [bold]{scenario.Name}[/]: {Markup.Escape(scenario.Description)}");
                    result.Started = DateTimeOffset.UtcNow;
                    try
                    {
                        await scenario.RunAsync(context, result, ct);
                    }
                    catch (Exception) when (ct.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                    }
                    catch (BenchSetupException ex)
                    {
                        result.Fail(ex.Message);
                    }
                    catch (Exception ex)
                    {
                        result.Fail($"{LatencyRecorder.Classify(ex)}: {ex.Message}");
                    }

                    result.Ended = DateTimeOffset.UtcNow;
                    Report.Print(result);
                }

                if (results.Count > 1) Report.Summary(results);
                string saved = Report.Save(context, results, settings.Out);
                AnsiConsole.MarkupLine($"[grey]Report saved to {Markup.Escape(Path.GetFullPath(saved))} (and .json).[/]");
                if (context.CreatedProject)
                    AnsiConsole.MarkupLine($"[grey]Project {Markup.Escape(context.ProjectName)} stays on the server (there is no project delete yet). Pass --project to reuse one.[/]");

                return results.All(r => r.Passed) ? 0 : 1;
            }
        }
    }
}
