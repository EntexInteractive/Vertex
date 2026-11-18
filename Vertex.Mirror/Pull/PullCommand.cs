// Copyright 2026 Entex Interactive

namespace Vertex.Mirror.Pull
{
    /// <summary>
    /// <c>vx-mirror pull</c>: git -> Vertex. See <see cref="GitPull"/>.
    /// </summary>
    public sealed class PullCommand : AsyncCommand<PullSettings>
    {
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);
        public const int DefaultReplay = 300;
        public const int DefaultFullCheckEvery = 12;

        public override async Task<int> ExecuteAsync(CommandContext context, PullSettings settings, CancellationToken cancellationToken)
        {
            string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            WorkFolder work = WorkFolder.Create(settings.Work, runId);
            try
            {
                using VertexConnection vertex = await VertexConnection.ConnectAsync(settings, true, cancellationToken);
                MirrorLog log = new(settings.Out, "pull", runId);
                GitPull pull = new(vertex, log, settings.Git!, work.Path);
                long problems = await pull.RunAsync(settings.IntervalOr(DefaultInterval), settings.ParsedDuration, settings.Start, settings.Replay ?? DefaultReplay,
                    settings.FullCheckEvery ?? DefaultFullCheckEvery, cancellationToken);
                return problems == 0 ? 0 : 1;
            }
            catch (MirrorSetupException ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return 2;
            }
            catch (ToolFailedException ex)
            {
                // git or p4 refused the first step (clone, connect): nothing to retry, so stop with its message.
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return 2;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("[yellow]Stopped before the mirror started.[/]");
                return 1;
            }
            finally
            {
                work.Dispose();
            }
        }
    }
}
