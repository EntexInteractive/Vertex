// Copyright 2026 Entex Interactive

using System.Globalization;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Mirror.Push
{
    /// <summary>
    /// Pushes Vertex revisions into a git branch: one commit per revision, authored by the revision's submitter at its
    /// time, with its message plus a <c>Vertex-Revision: N</c> trailer.
    /// </summary>
    public sealed class GitTarget : IPushTarget
    {
        private readonly string _repository;
        private readonly string _branch;
        private readonly string _authorDomain;
        private bool _unpublished;

        public GitTarget(string repository, string branch, string root, string server)
        {
            _repository = repository;
            _branch = branch;
            Root = root;
            _authorDomain = Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) ? uri.Host : "vertex";
        }

        public string Describe => $"git {_repository} ({_branch})";

        public string Root { get; }

        public async Task<long> SyncAsync(CancellationToken ct)
        {
            if (!Directory.Exists(Path.Combine(Root, ".git")))
            {
                WorkFolder.DeleteTree(Root);
                try
                {
                    await Git.RunAsync(Path.GetDirectoryName(Root), ct, "clone", "--quiet", "--no-checkout", _repository, Root);
                }
                catch (ToolFailedException ex)
                {
                    throw new MirrorSetupException($"Couldn't clone {_repository}: {ex.Message}. Check the address and that git can log in to it " +
                                                   "(it must work without prompting, e.g. with a credential helper or an SSH key), or pass another --git.");
                }

                Git.DisableConversions(Root);
            }
            else
            {
                await Git.RunAsync(Root, ct, "fetch", "--quiet", "--prune", "origin");
            }

            _unpublished = false;
            string remote = $"refs/remotes/origin/{_branch}";
            bool exists = await SucceedsAsync(ct, "rev-parse", "--verify", "--quiet", remote);
            if (exists)
            {
                // Exactly the remote branch: local commits that never got pushed are dropped and made again.
                await Git.RunAsync(Root, ct, "checkout", "--quiet", "--force", "-B", _branch, remote);
            }
            else
            {
                // A new branch (or an empty repository): start with no files and no history.
                await Git.RunAsync(Root, ct, "symbolic-ref", "HEAD", $"refs/heads/{_branch}");
                await SucceedsAsync(ct, "update-ref", "-d", $"refs/heads/{_branch}");
                await Git.RunAsync(Root, ct, "read-tree", "--empty");
            }

            await Git.RunAsync(Root, ct, "clean", "--quiet", "-d", "-x", "--force");
            if (!exists) return 0;

            string message = await Git.RunAsync(Root, ct, "log", "-1", $"--grep=^{Trailer.Name}: ", "--format=%B", "HEAD");
            if (Trailer.Find(message) is long revision) return revision;
            throw new MirrorSetupException($"{_branch} in {_repository} already has commits that didn't come from vx-mirror. " +
                                           "Push to an empty repository, or to a new branch with --git-branch <name>.");
        }

        public bool Accepts(string path, out string reason)
        {
            // git keeps its own data in .git; a file there would corrupt the clone, and git refuses to add it anyway.
            string first = path.Split('/', 2)[0];
            if (string.Equals(first, ".git", StringComparison.OrdinalIgnoreCase))
            {
                reason = "git can't store files in a .git folder";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public Task BeforeWriteAsync(IReadOnlyList<RevisionChange> changes, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public async Task<string?> CommitAsync(RevisionSummary revision, IReadOnlyList<RevisionChange> changes, CancellationToken ct)
        {
            // --force: files Vertex holds go in even when the mirrored .gitignore would ignore them.
            await Git.RunAsync(Root, ct, "add", "--all", "--force", ".");

            string messageFile = Path.Combine(Root, ".git", "VX_MIRROR_MESSAGE");
            await File.WriteAllTextAsync(messageFile, Trailer.Append(revision.Message, revision.Number), ct);
            string name = revision.DisplayName ?? revision.Username;
            string email = $"{revision.Username}@{_authorDomain}";
            string date = "@" + revision.CreatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " +0000";
            Dictionary<string, string> author = new()
            {
                ["GIT_AUTHOR_NAME"] = name,
                ["GIT_AUTHOR_EMAIL"] = email,
                ["GIT_AUTHOR_DATE"] = date,
                ["GIT_COMMITTER_NAME"] = name,
                ["GIT_COMMITTER_EMAIL"] = email,
                ["GIT_COMMITTER_DATE"] = date
            };

            // --allow-empty: every revision becomes a commit, even one that only touched files git can't hold, so the
            // history lines up one to one. --no-verify: hooks belong to people's clones, not to a mirror.
            await Git.Tool.RunAsync(Root, author, ct, "commit", "--quiet", "--allow-empty", "--no-verify", "--cleanup=verbatim", "--file", messageFile);
            File.Delete(messageFile);
            _unpublished = true;
            return Format.ShortSha(await Git.RunAsync(Root, ct, "rev-parse", "HEAD"));
        }

        public async Task PublishAsync(CancellationToken ct)
        {
            if (!_unpublished) return;
            await Git.RunAsync(Root, ct, "push", "--quiet", "origin", $"HEAD:refs/heads/{_branch}");
            _unpublished = false;
        }

        private async Task<bool> SucceedsAsync(CancellationToken ct, params string[] args)
        {
            try
            {
                await Git.RunAsync(Root, ct, args);
                return true;
            }
            catch (ToolFailedException)
            {
                return false;
            }
        }
    }
}
