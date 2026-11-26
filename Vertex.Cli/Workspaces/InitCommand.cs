// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;
using Vertex.Cli.Branches;
using Vertex.Cli.Files;
using Vertex.Core.Client.IO;
using Vertex.Core.Client.IO.IgnoreTemplates;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Workspaces
{
    public sealed class InitSettings : BranchSettings
    {
        [CommandOption("--template")]
        [Description("Starter .vxignore to write (e.g. 'unreal'), or 'none'. Detected from the folder by default.")]
        public string? Template { get; set; }

        [CommandOption("--upstream")]
        [Description("Takes the project's files from a branch of another project, as <project>/<branch> (e.g. UE5/release): a game on a studio engine. Its files are copied onto the new branch and downloaded here.")]
        public string? Upstream { get; set; }

        [CommandOption("--read-only")]
        [Description("With --upstream: refuses submits to the upstream's files in this project, so they're only changed upstream.")]
        public bool ReadOnly { get; set; }

        [CommandOption("--parallel")]
        [Description("With --upstream: the number of parallel downloads.")]
        public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

        public override ValidationResult Validate()
        {
            return ReadOnly && string.IsNullOrWhiteSpace(Upstream)
                ? ValidationResult.Error("--read-only goes with --upstream, e.g. 'vx init --upstream UE5/release --read-only'.")
                : ValidationResult.Success();
        }
    }

    /// <summary>
    /// Creates a new project with a default branch on the server, then turns the folder into its workspace.
    /// </summary>
    /// <remarks>
    /// The server comes first: if it can't be reached or the name is taken, nothing is created locally. To link a
    /// folder to a project that already exists, use <c>vx clone</c>.
    /// <para>
    /// With <c>--upstream</c>, the new branch takes its files from a branch of another project (a studio engine for a
    /// game): the server copies them onto it as the first revision, and they're downloaded into the folder next to the
    /// files already there, the way <c>vx clone</c> does (identical files are kept without downloading; different ones
    /// are reported and left alone).
    /// </para>
    /// </remarks>
    public sealed class InitCommand : AsyncCommand<InitSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, InitSettings settings, CancellationToken cancellationToken)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.RootPath));
            VWorkspace? existing = VWorkspace.Find(root);
            if (existing is not null)
            {
                string linkedTo = existing.Config.Project is null ? "" : $" (linked to {existing.Config.Root})";
                AnsiConsole.MarkupLine($"[red]'{Markup.Escape(existing.Root)}' is already a Vertex workspace{Markup.Escape(linkedTo)}.[/]");
                return 1;
            }

            IgnoreTemplate? template = null;
            if (settings.Template is not null && !settings.Template.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                template = IgnoreTemplate.Find(settings.Template);
                if (template is null)
                {
                    string available = string.Join(", ", IgnoreTemplate.All.Select(t => t.Name).Append("none"));
                    AnsiConsole.MarkupLine($"[red]Unknown template '{Markup.Escape(settings.Template)}'. Available: {Markup.Escape(available)}.[/]");
                    return 1;
                }
            }
            else if (settings.Template is null)
            {
                template = IgnoreTemplate.DetectFor(root);
            }

            string projectName = settings.Project ?? new DirectoryInfo(root).Name;
            string branchName = settings.Branch ?? "main";

            string? upstream = string.IsNullOrWhiteSpace(settings.Upstream) ? null : settings.Upstream.Trim();
            using ProjectInterface projects = new(settings.ServerAddress, settings.Token);
            ServerResult<CreateProjectResponse> result = null!;
            await AnsiConsole.Status().StartAsync(upstream is null ? "Creating the project..." : $"Creating the project from {Markup.Escape(upstream)}...", async _ =>
            {
                result = await projects.CreateProjectAsync(projectName, branchName, cancellationToken, upstream, settings.ReadOnly);
            });

            if (result.StatusCode == 409)
            {
                AnsiConsole.MarkupLine($"[red]Project '{Markup.Escape(projectName)}' already exists. Use 'vx clone {Markup.Escape(projectName)}' to get its files here.[/]");
                return 1;
            }

            if (upstream is not null && result.StatusCode is 403 or 404 or 400)
            {
                AnsiConsole.MarkupLine($"[red]Nothing was created: {Markup.Escape(result.Exception?.Message ?? "the upstream isn't available")}[/]");
                return 1;
            }

            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Could not create the project: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                Log.Error(result.Exception, "Failed to create project");
                return 1;
            }

            CreateProjectResponse created = result.Data!;
            VWorkspace workspace;
            try
            {
                workspace = VWorkspace.Initialize(root, new WorkspaceConfig
                {
                    Address = settings.ServerFromOption ? settings.ServerAddress : null,
                    Project = created.Project.Name,
                    ProjectId = created.Project.Id,
                    Branch = created.DefaultBranch.Name,
                    BranchId = created.DefaultBranch.Id,
                    User = settings.Credential?.Username
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[red]Project '{Markup.Escape(created.Project.Name)}' was created on the server, but the workspace could not be created: {Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine($"[grey]Fix the problem, then run 'vx clone {Markup.Escape(created.Project.Name)}' here to link it.[/]");
                return 1;
            }

            AnsiConsole.MarkupLine($"Created project [purple]{Markup.Escape(created.Project.Name)}[/] with branch [purple]{Markup.Escape(created.DefaultBranch.Name)}[/].");
            AnsiConsole.MarkupLine($"Initialized workspace in [purple]{Markup.Escape(workspace.MetadataDirectory)}[/]");

            int exit = 0;
            if (upstream is not null) exit = await TakeUpstreamAsync(settings, workspace, created, upstream, cancellationToken);

            if (template is not null)
            {
                if (File.Exists(Path.Combine(root, IgnoreRules.FileName)))
                {
                    AnsiConsole.MarkupLine($"[grey]Kept the existing {IgnoreRules.FileName}.[/]");
                }
                else
                {
                    template.WriteTo(root);
                    AnsiConsole.MarkupLine($"Wrote a {Markup.Escape(template.DisplayName)} {IgnoreRules.FileName}. Review it before your first submit.");
                }
            }

            return exit;
        }

        /// <summary>
        /// Reports the upstream the server linked and imported, then downloads its files next to the folder's own.
        /// </summary>
        private static async Task<int> TakeUpstreamAsync(InitSettings settings, VWorkspace workspace, CreateProjectResponse created, string upstream, CancellationToken cancellationToken)
        {
            string name = Markup.Escape(created.Upstream?.Name ?? upstream);
            if (created.Upstream is null || created.UpstreamError is not null)
            {
                string why = Markup.Escape(created.UpstreamError ?? "the server didn't link it");
                AnsiConsole.MarkupLine(created.Upstream is null
                    ? $"[yellow]The project was created, but linking {name} failed: {why}. Run 'vx upstream set {name}', then 'vx merge upstream'.[/]"
                    : $"[yellow]The project was created and linked to {name}, but taking in its files failed: {why}. Run 'vx merge upstream' to try again.[/]");
                return 1;
            }

            string mode = created.Upstream.ReadOnly ? ", read-only here" : "";
            AnsiConsole.MarkupLine(created.ImportRevision > 0
                ? $"Took in [purple]{name}[/] as [purple]#{created.ImportRevision}[/] ({created.ImportedFiles:N0} file(s){mode})."
                : $"Linked to [purple]{name}[/]{mode}. It has no files yet.");
            if (created.ImportRevision == 0) return 0;

            Stopwatch timer = Stopwatch.StartNew();
            LinkedWorkspace linked = LinkedWorkspace.TryFrom(workspace)!;
            using WorkspaceIndex index = workspace.OpenIndex();

            // Scan so files already in the folder (a local copy of the engine, the game's own) are adopted or kept.
            WorkspaceStatus status = null!;
            AnsiConsole.Status().Start("Scanning directory...", _ =>
            {
                status = WorkspaceScanner.Scan(workspace, index, new ScanOptions { MaxConcurrency = Math.Max(1, settings.MaxConcurrency) }, cancellationToken);
            });

            WorkspaceSettings sync = new() { ServerAddress = settings.ServerAddress, MaxConcurrency = settings.MaxConcurrency };
            SyncOutcome outcome;
            try
            {
                outcome = await WorkspaceSync.RunAsync(linked, index, status, sync, 0, cancellationToken);
            }
            catch (Exception)
            {
                AnsiConsole.MarkupLine($"[yellow]Not all of {name}'s files are downloaded. Run 'vx sync' to finish.[/]");
                throw;
            }

            timer.Stop();
            LockOutput.PrintError(outcome.Locks);
            AnsiConsole.MarkupLine($"Downloaded {outcome.Updated:N0} file(s) at [purple]#{outcome.Head.HeadRevision}[/].");
            WorkspaceSync.PrintTransfer(outcome, timer);
            WorkspaceSync.PrintConflicts(workspace, outcome.Conflicts);
            return outcome.Conflicts.Count == 0 ? 0 : 1;
        }
    }
}
