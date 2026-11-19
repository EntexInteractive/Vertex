// Copyright 2026 Entex Interactive

using System.Globalization;

namespace Vertex.Mirror.Pull
{
    /// <summary>
    /// Pulls from a git repository: clones it and walks its default branch's first-parent line, oldest first, so a
    /// merge is one change (the merge commit) like on the branch itself.
    /// </summary>
    public sealed class GitSource : IPullSource
    {
        private readonly string _repository;
        private readonly List<string> _history = new();
        private string _branch = string.Empty;
        private int _index;
        private SourceChange? _current;

        public GitSource(string repository, string root)
        {
            _repository = repository;
            Root = root;
        }

        public string Describe => _branch.Length == 0 ? $"git {_repository}" : $"git {_repository} ({_branch})";

        public string Root { get; }

        public string? SkipFolder => ".git";

        public string TrailerName => Trailer.GitCommit;

        public SourceChange Current => _current ?? throw new InvalidOperationException("Open the source first.");

        public int Remaining => _history.Count - 1 - _index;

        public async Task OpenAsync(string? start, string? resumeAt, CancellationToken ct)
        {
            // A reused --work folder starts clean.
            WorkFolder.DeleteTree(Root);
            AnsiConsole.MarkupLine($"[grey]Cloning {Markup.Escape(_repository)}...[/]");
            try
            {
                await Git.RunAsync(Path.GetDirectoryName(Root), ct, "clone", "--quiet", "--no-checkout", "--single-branch", _repository, Root);
            }
            catch (ToolFailedException ex)
            {
                throw new MirrorSetupException($"Couldn't clone {_repository}: {ex.Message}. Check the address and that git can log in to it without prompting, or pass another --git.");
            }

            Git.DisableConversions(Root);
            _branch = await Git.RunAsync(Root, ct, "symbolic-ref", "--short", "HEAD");
            try
            {
                // The branch's first-parent line, oldest first: commit N is _history[N - 1].
                _history.AddRange(Format.Lines(await Git.RunAsync(Root, ct, "rev-list", "--first-parent", "--reverse", "HEAD")));
            }
            catch (ToolFailedException)
            {
                throw new MirrorSetupException($"{_repository} has no commits yet.");
            }

            if (_history.Count == 0) throw new MirrorSetupException($"{_repository} has no commits yet.");
            _index = resumeAt is not null ? FindResume(resumeAt) : start is not null ? await FindStartAsync(start, ct) : 0;
            await CheckoutAsync(ct);
        }

        private int FindResume(string sha)
        {
            int index = _history.IndexOf(sha);
            if (index < 0)
                throw new MirrorSetupException($"The branch was last mirrored from commit {Format.ShortSha(sha)}, which is no longer on {_branch}'s main line in {_repository} " +
                                               "(its history was rewritten). Pass --start <commit number> to pick where to continue from.");
            return index;
        }

        private async Task<int> FindStartAsync(string start, CancellationToken ct)
        {
            if (start.Length < 7 && int.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                if (number < 1 || number > _history.Count)
                    throw new MirrorSetupException($"--start {number} is past the end: {_branch} has {_history.Count:N0} commit(s). Use a number from 1 to {_history.Count:N0}.");
                return number - 1;
            }

            string sha;
            try
            {
                sha = await Git.RunAsync(Root, ct, "rev-parse", "--verify", "--quiet", $"{start}^{{commit}}");
            }
            catch (ToolFailedException)
            {
                throw new MirrorSetupException($"--start '{start}' isn't a commit in {_repository}. Use a commit number (1 = the first commit), a sha or a tag.");
            }

            int index = _history.IndexOf(sha);
            if (index < 0)
                throw new MirrorSetupException($"--start '{start}' isn't on {_branch}'s main line (it may be inside a merged branch). Use a commit number instead, from 1 to {_history.Count:N0}.");
            return index;
        }

        public async Task<bool> AdvanceAsync(CancellationToken ct)
        {
            if (Remaining == 0)
            {
                // Caught up: look for new upstream commits. Offline for a while isn't a Vertex problem, so a failed
                // fetch just means nothing new this time.
                await Git.RunAsync(Root, ct, "fetch", "--quiet", "origin");
                _history.AddRange(Format.Lines(await Git.RunAsync(Root, ct, "rev-list", "--first-parent", "--reverse", $"{_history[_index]}..origin/{_branch}")));
                if (Remaining == 0) return false;
            }

            _index++;
            await CheckoutAsync(ct);
            return true;
        }

        private async Task CheckoutAsync(CancellationToken ct)
        {
            string sha = _history[_index];
            await Git.RunAsync(Root, ct, "checkout", "--quiet", "--force", "--detach", sha);

            // Author, time and raw message in one call, separated by NULs (which none of them can contain).
            string[] parts = (await Git.RunAsync(Root, ct, "log", "-1", "--format=%an <%ae>%x00%at%x00%B", sha)).Split('\0', 3);
            long seconds = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long at) ? at : 0;
            _current = new SourceChange(sha, $"commit {_index + 1} {Format.ShortSha(sha)}", parts.Length > 2 ? parts[2] : string.Empty, parts[0],
                DateTimeOffset.FromUnixTimeSeconds(seconds));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
