// Copyright 2026 Entex Interactive

namespace Vertex.Mirror.Pull
{
    /// <summary>
    /// One commit or changelist of a pull source.
    /// </summary>
    /// <param name="Id">What the trailer records: a git commit's full sha, a Perforce changelist number.</param>
    /// <param name="Label">For the console and log, e.g. "commit 12 1a2b3c4" or "change 4567".</param>
    /// <param name="Message">The commit message or changelist description, as written.</param>
    /// <param name="Author">Who made it, e.g. "Jane Doe &lt;jane@studio.com&gt;" or a Perforce user.</param>
    /// <param name="Time">When it was made.</param>
    public sealed record SourceChange(string Id, string Label, string Message, string Author, DateTimeOffset Time);

    /// <summary>
    /// A version control system that <see cref="PullRun"/> replays into Vertex (git, Perforce). It owns a working
    /// folder holding the files at <see cref="Current"/> and walks its history oldest first, one change at a time.
    /// </summary>
    /// <remarks>
    /// Every Vertex revision made from a change carries a <see cref="TrailerName"/> trailer with its
    /// <see cref="SourceChange.Id"/>, so a rerun resumes after the last one on the branch, with no state file to lose.
    /// </remarks>
    public interface IPullSource : IAsyncDisposable
    {
        /// <summary>
        /// Where the source is, for messages ("git https://... (main)", "Perforce //depot/game/...").
        /// </summary>
        string Describe { get; }

        /// <summary>
        /// The working folder: Vertex path <c>a/b.txt</c> is <c>Root/a/b.txt</c>.
        /// </summary>
        string Root { get; }

        /// <summary>
        /// A top-level folder in <see cref="Root"/> that belongs to the tool, not the files (git's <c>.git</c>), or null.
        /// </summary>
        string? SkipFolder { get; }

        /// <summary>
        /// The trailer that records <see cref="SourceChange.Id"/> on each Vertex revision.
        /// </summary>
        string TrailerName { get; }

        /// <summary>
        /// The change <see cref="Root"/> holds now.
        /// </summary>
        SourceChange Current { get; }

        /// <summary>
        /// Changes known to come after <see cref="Current"/>; 0 once caught up with the source's head.
        /// </summary>
        int Remaining { get; }

        /// <summary>
        /// Fetches the history and puts <see cref="Root"/> at the first change to mirror: <paramref name="resumeAt"/>
        /// (an id from a trailer) when given, else <paramref name="start"/>, else the very first change.
        /// </summary>
        /// <exception cref="MirrorSetupException">The source or a position can't be used; the message says what to change.</exception>
        Task OpenAsync(string? start, string? resumeAt, CancellationToken ct);

        /// <summary>
        /// Moves <see cref="Root"/> to the next change, checking the source for new ones once the known ones run out.
        /// </summary>
        /// <returns>True when it moved; false when there's nothing new yet.</returns>
        Task<bool> AdvanceAsync(CancellationToken ct);
    }
}
