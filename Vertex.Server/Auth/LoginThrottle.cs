// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;

namespace Vertex.Server.Auth
{
    /// <summary>
    /// Slows password guessing: after <see cref="MaximumFailures"/> failed logins for one username within
    /// <see cref="Window"/>, further attempts are refused until the window passes.
    /// </summary>
    /// <remarks>
    /// Keyed by username rather than IP address because behind a proxy such as Cloudflare Tunnel every client shares
    /// one address, and an IP limit would lock out the whole team. Only failures count, so normal logins are never
    /// limited. Kept per server instance, which is enough to make guessing impractical.
    /// </remarks>
    public sealed class LoginThrottle
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
        public const int MaximumFailures = 5;

        private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.OrdinalIgnoreCase);

        private sealed record Failures(int Count, DateTime FirstAt);

        public bool IsBlocked(string username)
        {
            return _failures.TryGetValue(username, out Failures? failures) && failures.Count >= MaximumFailures && DateTime.UtcNow - failures.FirstAt < Window;
        }

        public void RecordFailure(string username)
        {
            DateTime now = DateTime.UtcNow;
            _failures.AddOrUpdate(username, _ => new Failures(1, now), (_, f) => now - f.FirstAt >= Window ? new Failures(1, now) : f with { Count = f.Count + 1 });

            // Drop expired entries now and then so guessed usernames don't accumulate.
            if (_failures.Count > 10_000)
            {
                foreach (KeyValuePair<string, Failures> entry in _failures)
                    if (now - entry.Value.FirstAt >= Window) _failures.TryRemove(entry);
            }
        }

        public void RecordSuccess(string username)
        {
            _failures.TryRemove(username, out _);
        }
    }
}
