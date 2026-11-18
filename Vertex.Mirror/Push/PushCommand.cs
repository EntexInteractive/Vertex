// Copyright 2026 Entex Interactive

namespace Vertex.Mirror.Push
{
    /// <summary>
    /// <c>vx-mirror push</c>: Vertex -> git (--git) or Perforce (--p4). See <see cref="PushRun"/>.
    /// </summary>
    public sealed class PushCommand : AsyncCommand<PushSettings>
    {
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

        public override async Task<int> ExecuteAsync(CommandContext context, PushSettings settings, CancellationToken cancellationToken)
        {
            string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            WorkFolder work = WorkFolder.Create(settings.Work, runId);
            try
            {
                using VertexConnection vertex = await VertexConnection.ConnectAsync(settings, false, cancellationToken);
                IPushTarget target = settings.Git is not null
                    ? new GitTarget(settings.Git, settings.GitBranch ?? vertex.BranchName, Path.Combine(work.Path, "git"), vertex.Server)
                    : new PerforceTarget(settings.P4Client!, settings.P4Port, settings.P4User);
                MirrorLog log = new(settings.Out, "push", runId);
                long problems = await new PushRun(vertex, target, log).RunAsync(settings.IntervalOr(DefaultInterval), settings.ParsedDuration, settings.Once, cancellationToken);
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
