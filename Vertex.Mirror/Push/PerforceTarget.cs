// Copyright 2026 Entex Interactive

using System.Text;
using System.Text.RegularExpressions;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Mirror.Push
{
    /// <summary>
    /// Pushes Vertex revisions into Perforce through an existing client workspace: one submitted changelist per
    /// revision, described by the revision's message plus who submitted it, when, and a <c>Vertex-Revision: N</c>
    /// trailer. The workspace's root and view decide where files land in the depot.
    /// </summary>
    /// <remarks>
    /// Changelists are submitted by the Perforce user the mirror runs as; setting another user or date needs admin
    /// rights, so the original submitter and time go in the description instead.
    /// </remarks>
    public sealed partial class PerforceTarget : IPushTarget
    {
        private readonly string _client;
        private readonly ExternalTool _p4;
        private readonly List<string> _adds = new();
        private string _root = string.Empty;

        public PerforceTarget(string client, string? port, string? user)
        {
            _client = client;
            List<string> global = ["-c", client];
            if (port is not null) global.AddRange(["-p", port]);
            if (user is not null) global.AddRange(["-u", user]);
            _p4 = new ExternalTool("p4", "Install the Perforce command-line client (Helix Core P4) so that 'p4 -V' works, then run this again.", global);
        }

        public string Describe => $"Perforce workspace {_client}";

        public string Root => _root;

        /// <summary>
        /// Every file in the workspace's view, in depot syntax.
        /// </summary>
        private string View => $"//{_client}/...";

        public async Task<long> SyncAsync(CancellationToken ct)
        {
            if (_root.Length == 0) await OpenAsync(ct);

            // Undo whatever a failed step left open, and drop the empty pending changelists it left behind.
            await RunAsync(ct, ["file(s) not opened"], "revert", "-w", View);
            foreach (string change in Format.Lines(await RunAsync(ct, [], "-ztag", "-F", "%change%", "changes", "-s", "pending", "-c", _client)))
                await RunAsync(ct, [], "change", "-d", change);
            _adds.Clear();

            await RunAsync(ct, ["file(s) up-to-date", "no such file(s)"], "sync", "-q", View);

            // The newest submitted changelist with a trailer says how far the mirror got.
            string[] recent = Format.Lines(await RunAsync(ct, ["no such file(s)"], "-ztag", "-F", "%change%", "changes", "-s", "submitted", "-m", "50", View));
            foreach (string change in recent)
            {
                string description = await RunAsync(ct, [], "-ztag", "-F", "%desc%", "describe", "-s", change);
                if (Trailer.Find(description) is long revision) return revision;
            }

            if (recent.Length == 0) return 0;
            throw new MirrorSetupException($"{View} already has changelists that didn't come from vx-mirror (the newest is {recent[0]}). " +
                                           "Point the workspace's view at an empty depot folder, or use a new workspace.");
        }

        private async Task OpenAsync(CancellationToken ct)
        {
            string found = await RunAsync(ct, [], "-ztag", "-F", "%client%", "clients", "-e", _client);
            if (!Format.Lines(found).Contains(_client, StringComparer.OrdinalIgnoreCase))
                throw new MirrorSetupException($"There's no Perforce client workspace '{_client}' (P4PORT and P4USER come from p4's settings, or --p4-port/--p4-user). " +
                                               $"Create it with 'p4 client {_client}', with Root an empty folder and a View that maps the depot folder to mirror into, then run this again.");

            _root = await RunAsync(ct, [], "-ztag", "-F", "%Root%", "client", "-o");
            if (_root.Length == 0 || !Path.IsPathFullyQualified(_root))
                throw new MirrorSetupException($"Perforce workspace '{_client}' has no usable Root ('{_root}'). Set Root to an empty folder with 'p4 client {_client}'.");
            Directory.CreateDirectory(_root);
        }

        public bool Accepts(string path, out string reason)
        {
            // Perforce reserves "..." as its wildcard; no file name may contain it.
            if (path.Contains("...", StringComparison.Ordinal))
            {
                reason = "Perforce doesn't allow '...' in file names";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public async Task BeforeWriteAsync(IReadOnlyList<RevisionChange> changes, CancellationToken ct)
        {
            // The workspace is synced to the depot head, so a file that exists locally is in the depot: open it for
            // edit before it's overwritten (synced files are read-only) or for delete. New files are added after they
            // are written.
            List<string> edits = new(), deletes = new();
            foreach (RevisionChange change in changes)
            {
                string local = LocalPath(change.Path);
                bool exists = File.Exists(local);
                if (change.FileId is null)
                {
                    if (exists) deletes.Add(Escape(local));
                }
                else if (exists)
                {
                    edits.Add(Escape(local));
                }
                else
                {
                    _adds.Add(local);
                }
            }

            if (edits.Count > 0) await RunListAsync(edits, ct, "edit");
            if (deletes.Count > 0) await RunListAsync(deletes, ct, "delete");
        }

        public async Task<string?> CommitAsync(RevisionSummary revision, IReadOnlyList<RevisionChange> changes, CancellationToken ct)
        {
            // -f: names with @ # % * are taken literally.
            if (_adds.Count > 0) await RunListAsync(_adds, ct, "add", "-f");
            _adds.Clear();

            string opened = await RunAsync(ct, ["file(s) not opened"], "-ztag", "-F", "%depotFile%", "opened", View);
            if (opened.Length == 0) return null;

            string who = revision.DisplayName is null ? revision.Username : $"{revision.DisplayName} ({revision.Username})";
            string description = Trailer.Append(revision.Message, revision.Number, $"Submitted to Vertex by {who} at {revision.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC.");
            string output = await RunAsync(ct, [], "submit", "-d", description);
            Match match = SubmittedPattern().Match(output);
            return match.Success ? "change " + (match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value) : "submitted";
        }

        public Task PublishAsync(CancellationToken ct)
        {
            // Every changelist is on the server as soon as it's submitted.
            return Task.CompletedTask;
        }

        private string LocalPath(string path)
        {
            return Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// Perforce's escapes for the characters it reserves in file names (edit and delete need them; add -f doesn't).
        /// </summary>
        private static string Escape(string path)
        {
            return path.Replace("%", "%25").Replace("@", "%40").Replace("#", "%23").Replace("*", "%2A");
        }

        /// <summary>
        /// Runs a p4 command over a list of files passed in a file (-x), so any number of paths fits.
        /// </summary>
        private async Task RunListAsync(IReadOnlyList<string> files, CancellationToken ct, params string[] command)
        {
            string list = Path.GetTempFileName();
            try
            {
                await File.WriteAllLinesAsync(list, files, new UTF8Encoding(false), ct);
                await RunAsync(ct, [], ["-x", list, .. command]);
            }
            finally
            {
                File.Delete(list);
            }
        }

        /// <summary>
        /// Runs p4. A failure whose message contains one of <paramref name="harmless"/> (e.g. "file(s) up-to-date",
        /// which p4 reports as an error) counts as success with no output.
        /// </summary>
        private async Task<string> RunAsync(CancellationToken ct, string[] harmless, params string[] args)
        {
            try
            {
                return await _p4.RunAsync(null, ct, args);
            }
            catch (ToolFailedException ex) when (harmless.Any(h => ex.Output.Contains(h, StringComparison.OrdinalIgnoreCase)) && !ex.Output.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        [GeneratedRegex(@"Change (\d+) (?:renamed change (\d+) and )?submitted")]
        private static partial Regex SubmittedPattern();
    }
}
