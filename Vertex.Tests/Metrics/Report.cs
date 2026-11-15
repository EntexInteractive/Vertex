// Copyright 2026 Entex Interactive

using System.Runtime.InteropServices;
using System.Text;

namespace Vertex.Tests.Metrics
{
    /// <summary>
    /// Prints scenario results to the console and saves the run as JSON (for comparing runs) and Markdown (for
    /// reading or pasting).
    /// </summary>
    public static class Report
    {
        public static void Header(BenchContext context)
        {
            AnsiConsole.Write(new Rule("[purple]Vertex benchmark[/]").LeftJustified());
            Grid grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("[grey]Server[/]", Markup.Escape($"{context.Server} (version {context.ServerInfo?.Version ?? "?"}, built {context.ServerInfo?.BuildTimeUtc ?? "?"})"));
            grid.AddRow("[grey]User[/]", Markup.Escape(context.Username));
            grid.AddRow("[grey]Project[/]", Markup.Escape($"{context.ProjectName} ({(context.CreatedProject ? "created for this run" : "existing")}), paths under {context.PathPrefix}/"));
            grid.AddRow("[grey]Profile[/]", Markup.Escape(context.Profile.ToString()));
            grid.AddRow("[grey]Client[/]", Markup.Escape(ClientDescription()));
            grid.AddRow("[grey]Scratch[/]", Markup.Escape(context.DataRoot));
            AnsiConsole.Write(grid);
            AnsiConsole.WriteLine();
        }

        public static void Print(ScenarioResult result)
        {
            string status = result.Cancelled ? "[yellow]cancelled[/]" : result.Passed ? "[green]passed[/]" : "[red]failed[/]";
            AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(result.Name)}[/] {status} [grey]{Window(result)}[/]").LeftJustified());

            IReadOnlyList<LatencyRecorder> timings = Visible(result);
            if (timings.Count > 0)
            {
                Table table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("Call").AddColumn(new TableColumn("Count").RightAligned()).AddColumn(new TableColumn("Errors").RightAligned())
                    .AddColumn(new TableColumn("Mean ms").RightAligned()).AddColumn(new TableColumn("p50").RightAligned())
                    .AddColumn(new TableColumn("p95").RightAligned()).AddColumn(new TableColumn("p99").RightAligned())
                    .AddColumn(new TableColumn("Max").RightAligned());

                foreach (LatencySummary s in timings.Select(t => t.Summarize()))
                {
                    string errors = s.Errors == 0 ? "0" : $"[red]{s.Errors:N0}[/]";
                    table.AddRow(Markup.Escape(s.Name), s.Count.ToString("N0"), errors, Format.Milliseconds(s.Mean), Format.Milliseconds(s.P50),
                        Format.Milliseconds(s.P95), Format.Milliseconds(s.P99), Format.Milliseconds(s.Max));
                }

                AnsiConsole.Write(table);
                foreach (LatencySummary s in timings.Select(t => t.Summarize()).Where(s => s.ErrorsByKind.Count > 0))
                    AnsiConsole.MarkupLine($"  [red]{Markup.Escape(s.Name)} errors: {Markup.Escape(string.Join(", ", s.ErrorsByKind.Select(e => $"{e.Key} x{e.Value:N0}")))}[/]");
            }

            foreach (Figure figure in result.Figures)
                AnsiConsole.MarkupLine($"  {Markup.Escape(figure.Name)}: [bold]{Markup.Escape(figure.Value)}[/]");
            foreach (string note in result.Notes)
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(note)}[/]");
            foreach (string failure in result.Failures)
                AnsiConsole.MarkupLine($"  [red]FAIL: {Markup.Escape(failure)}[/]");
            AnsiConsole.WriteLine();
        }

        public static void Summary(IReadOnlyList<ScenarioResult> results)
        {
            Table table = new Table().Border(TableBorder.Simple).AddColumn("Scenario").AddColumn("Result").AddColumn("Window (UTC)")
                .AddColumn(new TableColumn("Errors").RightAligned());
            foreach (ScenarioResult result in results)
            {
                string status = result.Cancelled ? "[yellow]cancelled[/]" : result.Passed ? "[green]passed[/]" : $"[red]failed ({result.Failures.Count})[/]";
                table.AddRow(Markup.Escape(result.Name), status, Window(result), result.TotalErrors.ToString("N0"));
            }

            AnsiConsole.Write(table);
        }

        /// <summary>
        /// Writes bench-&lt;run&gt;.json and .md to <paramref name="folder"/>.
        /// </summary>
        /// <returns>The Markdown file's path.</returns>
        public static string Save(BenchContext context, IReadOnlyList<ScenarioResult> results, string folder)
        {
            Directory.CreateDirectory(folder);
            string basePath = Path.Combine(folder, $"bench-{context.RunId}");

            var document = new
            {
                run = context.RunId,
                server = context.Server,
                serverVersion = context.ServerInfo?.Version,
                serverBuild = context.ServerInfo?.BuildTimeUtc,
                user = context.Username,
                project = context.ProjectName,
                pathPrefix = context.PathPrefix,
                profile = context.Profile,
                client = ClientDescription(),
                scenarios = results.Select(r => new
                {
                    name = r.Name,
                    description = r.Description,
                    started = r.Started,
                    ended = r.Ended,
                    passed = r.Passed,
                    cancelled = r.Cancelled,
                    timings = r.Timings.Select(t => t.Summarize()),
                    figures = r.Figures,
                    failures = r.Failures,
                    notes = r.Notes
                })
            };

            File.WriteAllText(basePath + ".json", JsonConvert.SerializeObject(document, Formatting.Indented));
            File.WriteAllText(basePath + ".md", ToMarkdown(context, results));
            return basePath + ".md";
        }

        private static string ToMarkdown(BenchContext context, IReadOnlyList<ScenarioResult> results)
        {
            StringBuilder md = new();
            md.AppendLine($"# Vertex benchmark {context.RunId}");
            md.AppendLine();
            md.AppendLine($"- Server: {context.Server} (version {context.ServerInfo?.Version ?? "?"})");
            md.AppendLine($"- Project: {context.ProjectName}, paths under `{context.PathPrefix}/`");
            md.AppendLine($"- Profile: {context.Profile}");
            md.AppendLine($"- Client: {ClientDescription()}");
            md.AppendLine();
            md.AppendLine("| Scenario | Result | Window (UTC) | Errors |");
            md.AppendLine("|---|---|---|---:|");
            foreach (ScenarioResult r in results)
                md.AppendLine($"| {r.Name} | {(r.Cancelled ? "cancelled" : r.Passed ? "passed" : "failed")} | {Window(r)} | {r.TotalErrors:N0} |");

            foreach (ScenarioResult r in results)
            {
                md.AppendLine();
                md.AppendLine($"## {r.Name}");
                md.AppendLine();
                md.AppendLine($"{r.Description} ({Window(r)} UTC)");
                md.AppendLine();
                IReadOnlyList<LatencyRecorder> timings = Visible(r);
                if (timings.Count > 0)
                {
                    md.AppendLine("| Call | Count | Errors | Mean ms | p50 | p95 | p99 | Max |");
                    md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
                    foreach (LatencySummary s in timings.Select(t => t.Summarize()))
                        md.AppendLine($"| {s.Name} | {s.Count:N0} | {s.Errors:N0} | {Format.Milliseconds(s.Mean)} | {Format.Milliseconds(s.P50)} | " +
                                      $"{Format.Milliseconds(s.P95)} | {Format.Milliseconds(s.P99)} | {Format.Milliseconds(s.Max)} |");
                    md.AppendLine();
                }

                foreach (Figure f in r.Figures) md.AppendLine($"- {f.Name}: **{f.Value}**");
                foreach (LatencySummary s in timings.Select(t => t.Summarize()).Where(s => s.ErrorsByKind.Count > 0))
                    md.AppendLine($"- Errors in {s.Name}: {string.Join(", ", s.ErrorsByKind.Select(e => $"{e.Key} x{e.Value:N0}"))}");
                foreach (string n in r.Notes) md.AppendLine($"- _{n}_");
                foreach (string f in r.Failures) md.AppendLine($"- **FAIL:** {f}");
            }

            return md.ToString();
        }

        /// <summary>
        /// Timers that recorded something; error-only counters stay hidden while they have no errors.
        /// </summary>
        private static IReadOnlyList<LatencyRecorder> Visible(ScenarioResult result)
        {
            return result.Timings.Where(t => t.Count > 0 || t.ErrorCount > 0).ToArray();
        }

        private static string Window(ScenarioResult result)
        {
            if (result.Started == default) return "";
            return $"{result.Started.UtcDateTime:HH:mm:ss}-{result.Ended.UtcDateTime:HH:mm:ss}";
        }

        private static string ClientDescription()
        {
            return $"{Environment.MachineName}, {Environment.ProcessorCount} cores, {RuntimeInformation.OSDescription}, .NET {Environment.Version}";
        }
    }
}
