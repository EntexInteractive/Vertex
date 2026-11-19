// Copyright 2026 Entex Interactive

using System.Text;

namespace Vertex.Mirror
{
    /// <summary>
    /// The p4 command line for one client workspace, shared by push (<c>PerforceTarget</c>) and pull
    /// (<c>PerforceSource</c>). P4PORT and P4USER come from p4's own settings unless given.
    /// </summary>
    public sealed class P4
    {
        private readonly ExternalTool _tool;

        public P4(string client, string? port, string? user)
        {
            Client = client;
            List<string> global = ["-c", client];
            if (port is not null) global.AddRange(["-p", port]);
            if (user is not null) global.AddRange(["-u", user]);
            _tool = new ExternalTool("p4", "Install the Perforce command-line client (Helix Core P4) so that 'p4 -V' works, then run this again.", global);
        }

        public string Client { get; }

        /// <summary>
        /// Runs p4. A failure whose message contains one of <paramref name="harmless"/> (e.g. "file(s) up-to-date",
        /// which p4 reports as an error) counts as success with no output.
        /// </summary>
        public async Task<string> RunAsync(CancellationToken ct, string[] harmless, params string[] args)
        {
            try
            {
                return await _tool.RunAsync(null, ct, args);
            }
            catch (ToolFailedException ex) when (harmless.Any(h => ex.Output.Contains(h, StringComparison.OrdinalIgnoreCase)) && !ex.Output.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Runs p4 with <paramref name="input"/> on standard input, e.g. a spec for <c>p4 client -i</c>.
        /// </summary>
        public Task<string> RunWithInputAsync(string input, CancellationToken ct, params string[] args)
        {
            return _tool.RunWithInputAsync(null, input, ct, args);
        }

        /// <summary>
        /// Runs a p4 command over a list of files passed in a file (-x), so any number of paths fits.
        /// </summary>
        public async Task RunListAsync(IReadOnlyList<string> files, CancellationToken ct, params string[] command)
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
        /// Perforce's escapes for the characters it reserves in file names (edit and delete need them; add -f doesn't).
        /// </summary>
        public static string Escape(string path)
        {
            return path.Replace("%", "%25").Replace("@", "%40").Replace("#", "%23").Replace("*", "%2A");
        }

        /// <summary>
        /// The fields of one <c>p4 -ztag</c> record ("... name value" lines), first value per name. Multi-line values
        /// (a description) continue on the lines after their "... desc" line.
        /// </summary>
        public static Dictionary<string, string> ParseTagged(string output)
        {
            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            string? current = null;
            StringBuilder value = new();
            foreach (string line in output.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("... ", StringComparison.Ordinal))
                {
                    if (current is not null) fields.TryAdd(current, value.ToString().TrimEnd('\n'));
                    string rest = line[4..];
                    int space = rest.IndexOf(' ');
                    current = space < 0 ? rest : rest[..space];
                    value.Clear().Append(space < 0 ? string.Empty : rest[(space + 1)..]);
                }
                else if (current is not null)
                {
                    value.Append('\n').Append(line);
                }
            }

            if (current is not null) fields.TryAdd(current, value.ToString().TrimEnd('\n'));
            return fields;
        }
    }
}
