// Copyright 2026 Entex Interactive

using System.Globalization;
using System.Text;

namespace Vertex.Mirror.Pull
{
    /// <summary>
    /// Pulls from a Perforce depot path: makes its own client workspace mapping the path into the working folder, and
    /// syncs it to each submitted changelist that touched the path, oldest first. Only changed files transfer.
    /// </summary>
    /// <remarks>
    /// The client is named <c>vx-mirror-&lt;machine&gt;-&lt;run&gt;</c> and deleted at the end of the run. Its LineEnd is
    /// <c>share</c> so text files keep their exact bytes, and <c>nomodtime</c> so a changed file always gets a new write
    /// time (the scan's stat cache relies on it).
    /// </remarks>
    public sealed class PerforceSource : IPullSource
    {
        private static readonly string[] Quiet = ["file(s) up-to-date", "no such file(s)", "no file(s) at that changelist number"];

        private readonly string _path;
        private readonly P4 _p4;
        private readonly List<long> _changes = new();
        private bool _clientMade;
        private int _index;
        private SourceChange? _current;

        /// <param name="path">A depot path, e.g. <c>//depot/game</c> or <c>//depot/game/...</c>.</param>
        public PerforceSource(string path, string? port, string? user, string root, string runId)
        {
            path = path.Trim().TrimEnd('/');
            _path = path.EndsWith("/...", StringComparison.Ordinal) ? path : path + "/...";
            Root = root;
            string machine = new(Environment.MachineName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
            _p4 = new P4($"vx-mirror-{machine}-{runId}", port, user);
        }

        public string Describe => $"Perforce {_path}";

        public string Root { get; }

        public string? SkipFolder => null;

        public string TrailerName => Trailer.PerforceChange;

        public SourceChange Current => _current ?? throw new InvalidOperationException("Open the source first.");

        public int Remaining => _changes.Count - 1 - _index;

        public async Task OpenAsync(string? start, string? resumeAt, CancellationToken ct)
        {
            await CreateClientAsync(ct);
            AnsiConsole.MarkupLine($"[grey]Listing changelists in {Markup.Escape(_path)}...[/]");
            _changes.AddRange(await ListChangesAsync(_path, ct));
            if (_changes.Count == 0)
                throw new MirrorSetupException($"{_path} has no submitted changelists, or the Perforce user can't see it. Check the depot path, and P4PORT and P4USER (or --p4-port/--p4-user).");

            if (resumeAt is not null)
            {
                _index = long.TryParse(resumeAt, NumberStyles.None, CultureInfo.InvariantCulture, out long resume) ? _changes.IndexOf(resume) : -1;
                if (_index < 0)
                    throw new MirrorSetupException($"The branch was last mirrored from change {resumeAt}, which isn't a submitted changelist in {_path} any more. " +
                                                   "Pass --start <changelist> to pick where to continue from.");
            }
            else if (start is not null)
            {
                if (!long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
                    throw new MirrorSetupException($"--start '{start}' isn't a changelist number. Use a number, e.g. --start 1 for the first changelist.");
                _index = _changes.FindIndex(c => c >= number);
                if (_index < 0)
                    throw new MirrorSetupException($"--start {number} is past the end: the newest changelist in {_path} is {_changes[^1]}. Use a number up to {_changes[^1]}.");
            }
            else
            {
                _index = 0;
            }

            await SyncAsync(ct);
        }

        /// <summary>
        /// Makes the mirror's own client: Root the working folder, one view line for the depot path.
        /// </summary>
        private async Task CreateClientAsync(CancellationToken ct)
        {
            // A reused --work folder starts clean; the new client's have list is empty, so the first sync writes everything.
            WorkFolder.DeleteTree(Root);
            Directory.CreateDirectory(Root);
            string spec = new StringBuilder()
                .Append("Client: ").Append(_p4.Client).Append('\n')
                .Append("Root: ").Append(Root).Append('\n')
                .Append("Options: allwrite clobber nocompress unlocked nomodtime rmdir\n")
                .Append("SubmitOptions: submitunchanged\n")
                .Append("LineEnd: share\n")
                .Append("Description:\n\tMade by vx-mirror pull for ").Append(_path).Append("; deleted when the run ends.\n")
                .Append("View:\n\t\"").Append(_path).Append("\" \"//").Append(_p4.Client).Append("/...\"\n")
                .ToString();
            try
            {
                await _p4.RunWithInputAsync(spec, ct, "client", "-i");
            }
            catch (ToolFailedException ex)
            {
                throw new MirrorSetupException($"Couldn't make the Perforce client workspace {_p4.Client}: {ex.Message}. " +
                                               "Check P4PORT and P4USER (or --p4-port/--p4-user) and that you're logged in ('p4 login').");
            }

            _clientMade = true;
        }

        /// <summary>
        /// Submitted changelists that touched <paramref name="range"/>, oldest first.
        /// </summary>
        private async Task<List<long>> ListChangesAsync(string range, CancellationToken ct)
        {
            string output = await _p4.RunAsync(ct, Quiet, "-ztag", "-F", "%change%", "changes", "-s", "submitted", range);
            List<long> changes = Format.Lines(output).Select(line => long.Parse(line, CultureInfo.InvariantCulture)).ToList();
            changes.Reverse();
            return changes;
        }

        public async Task<bool> AdvanceAsync(CancellationToken ct)
        {
            if (Remaining == 0)
            {
                _changes.AddRange(await ListChangesAsync($"{_path}@{_changes[_index] + 1},@now", ct));
                if (Remaining == 0) return false;
            }

            _index++;
            await SyncAsync(ct);
            return true;
        }

        private async Task SyncAsync(CancellationToken ct)
        {
            long change = _changes[_index];
            await _p4.RunAsync(ct, Quiet, "sync", "-q", $"{_path}@{change}");

            string text = change.ToString(CultureInfo.InvariantCulture);
            Dictionary<string, string> fields = P4.ParseTagged(await _p4.RunAsync(ct, [], "-ztag", "changes", "-l", "-s", "submitted", "-m", "1", $"{_path}@{text},@{text}"));
            long seconds = fields.TryGetValue("time", out string? time) && long.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out long at) ? at : 0;
            string user = fields.GetValueOrDefault("user", "unknown");
            string client = fields.GetValueOrDefault("client", string.Empty);
            _current = new SourceChange(text, $"change {text}", fields.GetValueOrDefault("desc", string.Empty), client.Length == 0 ? user : $"{user}@{client}",
                DateTimeOffset.FromUnixTimeSeconds(seconds));
        }

        public async ValueTask DisposeAsync()
        {
            if (!_clientMade) return;
            try
            {
                await _p4.RunAsync(CancellationToken.None, [], "client", "-d", _p4.Client);
            }
            catch (Exception ex) when (ex is ToolFailedException or MirrorSetupException)
            {
                AnsiConsole.MarkupLine($"[yellow]Couldn't delete the Perforce client {Markup.Escape(_p4.Client)}: {Markup.Escape(ex.Message)}. Delete it with 'p4 client -d {Markup.Escape(_p4.Client)}'.[/]");
            }
        }
    }
}
