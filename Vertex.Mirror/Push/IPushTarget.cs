// Copyright 2026 Entex Interactive

using System.Text.RegularExpressions;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Mirror.Push
{
    /// <summary>
    /// A version control system that Vertex revisions are pushed into (git, Perforce). It owns a working folder whose
    /// files <see cref="PushRun"/> writes, and turns each revision into one commit or changelist.
    /// </summary>
    /// <remarks>
    /// The target itself remembers how far the mirror got: every commit carries a <c>Vertex-Revision: N</c> trailer, so
    /// a rerun (or a recovery after a failure) continues after the last one it finds, with no state file to lose.
    /// </remarks>
    public interface IPushTarget
    {
        /// <summary>
        /// Where the target is, for messages ("git https://... (main)").
        /// </summary>
        string Describe { get; }

        /// <summary>
        /// The working folder: Vertex path <c>a/b.txt</c> is <c>Root/a/b.txt</c>.
        /// </summary>
        string Root { get; }

        /// <summary>
        /// Brings the working folder to the target's latest state, discarding anything left over from a failed step.
        /// </summary>
        /// <returns>The last Vertex revision the target holds, or 0 when nothing was mirrored yet.</returns>
        /// <exception cref="MirrorSetupException">The target can't be used as is; the message says what to change.</exception>
        Task<long> SyncAsync(CancellationToken ct);

        /// <summary>
        /// False (with the reason) for a path this target can't hold, e.g. git's own <c>.git</c> folder.
        /// </summary>
        bool Accepts(string path, out string reason);

        /// <summary>
        /// Called before the runner writes or deletes a revision's files (Perforce opens them for edit or delete).
        /// </summary>
        Task BeforeWriteAsync(IReadOnlyList<RevisionChange> changes, CancellationToken ct);

        /// <summary>
        /// Records the written files as one commit or changelist.
        /// </summary>
        /// <returns>Its id (a sha or a change number), or null when the revision changed nothing this target holds.</returns>
        Task<string?> CommitAsync(RevisionSummary revision, IReadOnlyList<RevisionChange> changes, CancellationToken ct);

        /// <summary>
        /// Publishes the commits made since the last call (git push). Called after each batch of revisions.
        /// </summary>
        Task PublishAsync(CancellationToken ct);
    }

    public static partial class Trailer
    {
        public const string Name = "Vertex-Revision";

        /// <summary>
        /// The revision's message with the trailer that marks where it came from.
        /// </summary>
        public static string Append(string message, long revision, string? extra = null)
        {
            string body = message.TrimEnd();
            return $"{body}\n\n{(extra is null ? "" : extra + "\n")}{Name}: {revision}\n";
        }

        /// <summary>
        /// The revision named by the last trailer in one commit message, or null when it has none. The last one
        /// counts: a message that already quoted a trailer (a commit pulled from another mirror) gets ours after it.
        /// </summary>
        public static long? Find(string message)
        {
            MatchCollection matches = Pattern().Matches(message);
            return matches.Count == 0 ? null : long.Parse(matches[^1].Groups[1].Value);
        }

        [GeneratedRegex(@"^\s*Vertex-Revision: (\d+)\s*$", RegexOptions.Multiline)]
        private static partial Regex Pattern();
    }
}
