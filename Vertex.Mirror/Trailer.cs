// Copyright 2026 Entex Interactive

using System.Text.RegularExpressions;

namespace Vertex.Mirror
{
    /// <summary>
    /// The <c>Name: value</c> line at the end of a mirrored commit, changelist or revision message that says where it
    /// came from. The mirrored side itself remembers how far a mirror got, so reruns need no state file.
    /// </summary>
    public static partial class Trailer
    {
        /// <summary>On git commits and Perforce changelists made by push: the Vertex revision number.</summary>
        public const string VertexRevision = "Vertex-Revision";

        /// <summary>On Vertex revisions made by pull from git: the commit's full sha.</summary>
        public const string GitCommit = "Git-Commit";

        /// <summary>On Vertex revisions made by pull from Perforce: the changelist number.</summary>
        public const string PerforceChange = "Perforce-Change";

        /// <summary>
        /// The message with the trailer, after an optional line of <paramref name="extra"/> detail.
        /// </summary>
        public static string Append(string message, string name, string value, string? extra = null)
        {
            string body = message.TrimEnd();
            return $"{body}\n\n{(extra is null ? "" : extra + "\n")}{name}: {value}\n";
        }

        /// <summary>
        /// The value of the last <paramref name="name"/> trailer in one message, or null when it has none. The last one
        /// counts: a message that already quoted a trailer (one mirrored from another mirror) gets ours after it.
        /// </summary>
        public static string? Find(string message, string name)
        {
            string? found = null;
            foreach (Match match in Pattern().Matches(message))
            {
                if (match.Groups[1].Value == name) found = match.Groups[2].Value;
            }

            return found;
        }

        /// <summary>
        /// The revision named by the last <c>Vertex-Revision</c> trailer, or null.
        /// </summary>
        public static long? FindRevision(string message)
        {
            return Find(message, VertexRevision) is string value && long.TryParse(value, out long revision) ? revision : null;
        }

        [GeneratedRegex(@"^\s*([A-Za-z-]+): (\S+)\s*$", RegexOptions.Multiline)]
        private static partial Regex Pattern();
    }
}
