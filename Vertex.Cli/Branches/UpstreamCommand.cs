// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Text.Json;
using Vertex.Cli.Files;
using Vertex.Cli.Net;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Branches
{
    public sealed class UpstreamSettings : NetworkSettings
    {
        [CommandOption("--json")]
        [Description("Prints the upstream as JSON (project, branch and the revision taken in), for build scripts.")]
        public bool Json { get; set; }
    }

    public sealed class UpstreamSetSettings : NetworkSettings
    {
        [CommandArgument(0, "<upstream>")]
        [Description("The branch to take files from, as <project>/<branch> (e.g. UE5/release). A bare project name means its 'main'.")]
        public string Upstream { get; set; } = string.Empty;

        [CommandOption("--read-only")]
        [Description("Refuses submits to the upstream's files on this branch, so they're only changed upstream.")]
        public bool ReadOnly { get; set; }

        [CommandOption("--writable")]
        [Description("Allows changing the upstream's files on this branch again (the default).")]
        public bool Writable { get; set; }

        [CommandOption("--paths")]
        [Description("The folders and files the upstream owns, comma-separated (e.g. Engine,Templates), or 'auto' for its top-level ones (the default). Only these are sent up by 'vx merge --into upstream'.")]
        public string? Paths { get; set; }

        public override ValidationResult Validate()
        {
            return ReadOnly && Writable ? ValidationResult.Error("Pick one: --read-only or --writable.") : ValidationResult.Success();
        }
    }

    /// <summary>
    /// <c>vx upstream</c>: shows where the workspace's branch takes its files from and how far behind it is.
    /// </summary>
    /// <remarks>
    /// An upstream is a branch of another project (a studio engine for a game). The branch holds its own copy of the
    /// upstream's files, takes new upstream revisions with <c>vx merge upstream</c> and sends its own changes to them
    /// back with <c>vx merge --into upstream</c>.
    /// </remarks>
    public sealed class UpstreamCommand : AsyncCommand<UpstreamSettings>
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        public override async Task<int> ExecuteAsync(CommandContext context, UpstreamSettings settings, CancellationToken cancellationToken)
        {
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            string branch = linked.Workspace.Config.Branch ?? "this branch";
            using BranchInterface branches = new(settings.ServerAddress, settings.Token);

            VUpstream? upstream = null;
            await AnsiConsole.Status().StartAsync("Comparing with the upstream...", async _ =>
            {
                upstream = await UpstreamOutput.GetAsync(branches, linked, true, cancellationToken);
            });

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    project = linked.Workspace.Config.Project,
                    branch = linked.Workspace.Config.Branch,
                    upstream = upstream is null
                        ? null
                        : new
                        {
                            project = upstream.UpstreamProject, branch = upstream.UpstreamBranch, revision = upstream.TakenRevision, latest = upstream.UpstreamHead,
                            behind = upstream.Behind, differing = upstream.Differing, paths = upstream.Paths, readOnly = upstream.ReadOnly
                        }
                }, Json));
                return 0;
            }

            if (upstream is null)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(branch)} has no upstream. To take its files from another project's branch, run 'vx upstream set <project>/<branch>'.[/]");
                return 0;
            }

            string name = Markup.Escape(upstream.Name);
            string paths = upstream.Paths is { Length: > 0 } listed ? string.Join(", ", listed.Select(Markup.Escape)) : "its top-level folders and files";
            AnsiConsole.MarkupLine($"[purple]{Markup.Escape(branch)}[/] takes its files from [purple]{name}[/] ({(upstream.ReadOnly ? "read-only here" : "writable")}; it owns {paths}).");

            if (upstream.TakenRevision is not long taken)
            {
                AnsiConsole.MarkupLine($"  Its files aren't here yet. Run 'vx merge upstream' to take them in.");
                return 0;
            }

            AnsiConsole.MarkupLine(upstream.Behind > 0
                ? $"  At [purple]{name} #{taken}[/], [yellow]{upstream.Behind:N0} revision(s) behind[/] (latest #{upstream.UpstreamHead}). Run 'vx merge upstream' to take them in."
                : $"  Up to date with [purple]{name} #{taken}[/].");

            if (upstream.Differing > 0)
            {
                AnsiConsole.MarkupLine(upstream.Behind > 0
                    ? $"  {upstream.Differing:N0} of its file(s) differ from {name}'s latest, counting what this branch is behind on."
                    : $"  {upstream.Differing:N0} of its file(s) changed here and not sent up. Run 'vx merge --into upstream' to send them to {name}.");
            }

            return 0;
        }
    }

    /// <summary>
    /// <c>vx upstream set &lt;project&gt;/&lt;branch&gt;</c>: links the workspace's branch to an upstream, or changes the link.
    /// </summary>
    public sealed class UpstreamSetCommand : AsyncCommand<UpstreamSetSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, UpstreamSetSettings settings, CancellationToken cancellationToken)
        {
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            string branch = Markup.Escape(linked.Workspace.Config.Branch ?? "this branch");
            (string project, string upstreamBranch) = VUpstream.ParseName(settings.Upstream);
            bool? readOnly = settings.ReadOnly ? true : settings.Writable ? false : null;

            using BranchInterface branches = new(settings.ServerAddress, settings.Token);
            VUpstream? before = await UpstreamOutput.GetAsync(branches, linked, false, cancellationToken);
            ServerResult<VUpstream> result = await branches.SetUpstreamAsync(linked.ProjectId, linked.BranchId, project, upstreamBranch, settings.Paths?.Trim(), readOnly,
                cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Could not set the upstream: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                return 1;
            }

            VUpstream link = result.Data!;
            string name = Markup.Escape(link.Name);
            string mode = link.ReadOnly ? "read-only here" : "writable";
            if (before is not null && before.UpstreamBranchId == link.UpstreamBranchId)
            {
                AnsiConsole.MarkupLine($"[purple]{branch}[/] takes its files from [purple]{name}[/] ({mode}).");
                return 0;
            }

            string replaced = before is null ? "" : $" instead of {Markup.Escape(before.Name)}";
            AnsiConsole.MarkupLine($"[purple]{branch}[/] now takes its files from [purple]{name}[/]{replaced} ({mode}).");
            AnsiConsole.MarkupLine($"Run 'vx merge upstream' to take its files in. Files both have with different content are listed for you to settle.");
            return 0;
        }
    }

    /// <summary>
    /// <c>vx upstream clear</c>: unlinks the workspace's branch from its upstream. Its files stay.
    /// </summary>
    public sealed class UpstreamClearCommand : AsyncCommand<NetworkSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, NetworkSettings settings, CancellationToken cancellationToken)
        {
            LinkedWorkspace linked = LinkedWorkspace.FromCurrentDirectory();
            string branch = Markup.Escape(linked.Workspace.Config.Branch ?? "this branch");
            using BranchInterface branches = new(settings.ServerAddress, settings.Token);
            ServerResult<IReadOnlyList<VUpstream>> result = await branches.ClearUpstreamAsync(linked.ProjectId, linked.BranchId, cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Could not clear the upstream: {Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                return 1;
            }

            AnsiConsole.MarkupLine(result.Data!.Count == 0
                ? $"[grey]{branch} has no upstream.[/]"
                : $"{branch} no longer takes its files from {Markup.Escape(result.Data[0].Name)}. Its files stay as they are.");
            return 0;
        }
    }

    /// <summary>
    /// Upstream lookups shared by <c>vx upstream</c>, <c>vx merge upstream</c> and the hint <c>vx sync</c> prints.
    /// </summary>
    internal static class UpstreamOutput
    {
        /// <summary>The word that names the workspace branch's upstream in <c>vx merge</c>.</summary>
        public const string Keyword = "upstream";

        /// <summary>
        /// The workspace branch's upstream, or null when it has none.
        /// </summary>
        /// <param name="differences">Also count the upstream's files that differ (slower: the server compares both file lists).</param>
        /// <exception cref="InvalidOperationException">The server couldn't be asked.</exception>
        public static async Task<VUpstream?> GetAsync(BranchInterface branches, LinkedWorkspace linked, bool differences, CancellationToken cancellationToken)
        {
            ServerResult<IReadOnlyList<VUpstream>> result = await branches.GetUpstreamsAsync(linked.ProjectId, linked.BranchId, differences, cancellationToken);
            if (!result.IsSuccessStatusCode) throw new InvalidOperationException($"Could not get the upstream: {result.Exception?.Message ?? "Unknown error"}");
            return result.Data!.Count == 0 ? null : result.Data[0];
        }

        /// <summary>
        /// The upstream as a branch to merge with, or null (with the fix printed) when the workspace's branch has none.
        /// </summary>
        public static async Task<VBranch?> FindBranchAsync(BranchInterface branches, LinkedWorkspace linked, CancellationToken cancellationToken)
        {
            VUpstream? upstream = await GetAsync(branches, linked, false, cancellationToken);
            if (upstream is not null) return new VBranch(upstream.UpstreamBranchId, 0, upstream.Name, upstream.UpstreamProjectId, null);

            AnsiConsole.MarkupLine($"[red]{Markup.Escape(linked.Workspace.Config.Branch ?? "This branch")} has no upstream. Link one with 'vx upstream set <project>/<branch>'.[/]");
            return null;
        }

        /// <summary>
        /// One grey line when the workspace branch's upstream has revisions it hasn't taken in. Says nothing when it's
        /// up to date, has no upstream, or the server doesn't answer quickly: it's only a hint.
        /// </summary>
        public static async Task PrintBehindAsync(NetworkSettings settings, LinkedWorkspace linked, CancellationToken cancellationToken)
        {
            using CancellationTokenSource quick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quick.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using BranchInterface branches = new(settings.ServerAddress, settings.Token);
                VUpstream? upstream = await GetAsync(branches, linked, false, quick.Token);
                if (upstream is null) return;
                if (upstream.TakenRevision is null)
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(upstream.Name)}'s files aren't on this branch yet. Run 'vx merge upstream' to take them in.[/]");
                else if (upstream.Behind > 0)
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(upstream.Name)} has {upstream.Behind:N0} new revision(s). Run 'vx merge upstream' to take them in.[/]");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Only a hint.
            }
        }
    }
}
