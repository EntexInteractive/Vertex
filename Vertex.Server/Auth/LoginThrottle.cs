// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using Vertex.Server.Database;

namespace Vertex.Server.Auth
{
    /// <summary>
    /// Slows password guessing by counting failed logins, both per username and per client address. Once either
    /// count reaches its limit within <see cref="Window"/>, further attempts are refused until the window passes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The username limit stops guessing one account's password. The address limit stops trying one password against
    /// many accounts, which never trips the username limit. Only failures count, so normal logins are never limited.
    /// </para>
    /// <para>
    /// Counts live in Redis, so the limits hold across every server instance and restarts. Without Redis they fall
    /// back to this instance's memory.
    /// </para>
    /// <para>
    /// The address is the real client's only when proxies in front of the server are listed in
    /// <c>VX_TRUSTED_PROXIES</c>; otherwise every client behind the proxy shares one address. The address limit is
    /// set high enough that a team mistyping passwords won't reach it even then.
    /// </para>
    /// </remarks>
    public sealed class LoginThrottle
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
        public const int MaximumUserFailures = 10;
        public const int MaximumAddressFailures = 30;

        private readonly CacheContext _cache;
        private readonly ILogger<LoginThrottle> _logger;
        private readonly ConcurrentDictionary<string, Failures> _localFailures = new(StringComparer.Ordinal);
        private int _warnedUntrustedProxy;

        private sealed record Failures(long Count, DateTime FirstAt);

        public LoginThrottle(CacheContext cache, ILogger<LoginThrottle> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// Checks whether a login attempt is allowed.
        /// </summary>
        /// <returns>Null when allowed, otherwise the message to send back.</returns>
        public async Task<string?> CheckAsync(string username, HttpContext context)
        {
            string userKey = UserKey(username);
            string? addressKey = AddressKey(context);

            long[] counts = addressKey is null
                ? await GetCountsAsync(userKey)
                : await GetCountsAsync(userKey, addressKey);

            if (counts[0] >= MaximumUserFailures)
                return $"Too many failed logins for '{username}'. Wait {Window.TotalMinutes:N0} minutes and try again.";

            if (counts.Length > 1 && counts[1] >= MaximumAddressFailures)
                return $"Too many failed logins from your address. Wait {Window.TotalMinutes:N0} minutes and try again.";

            return null;
        }

        public async Task RecordFailureAsync(string username, HttpContext context)
        {
            await IncrementAsync(UserKey(username));

            string? addressKey = AddressKey(context);
            if (addressKey is not null) await IncrementAsync(addressKey);
        }

        /// <summary>
        /// Clears the username's count. The address count stays, so logging in to one account can't reset a run of
        /// guesses against others.
        /// </summary>
        public async Task RecordSuccessAsync(string username)
        {
            string key = UserKey(username);
            _localFailures.TryRemove(key, out _);
            await _cache.DeleteCounterAsync(key);
        }

        private static string UserKey(string username)
        {
            return "auth:login:user:" + username.ToLowerInvariant();
        }

        /// <summary>
        /// The counter key for the client's address. IPv6 addresses are grouped by /64, the block a single site or
        /// customer usually gets, so cycling through addresses inside it doesn't reset the count.
        /// </summary>
        private string? AddressKey(HttpContext context)
        {
            WarnIfBehindUntrustedProxy(context);

            IPAddress? address = context.Connection.RemoteIpAddress;
            if (address is null) return null;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] bytes = address.GetAddressBytes();
                Array.Clear(bytes, 8, 8);
                return "auth:login:ip:" + new IPAddress(bytes) + "/64";
            }

            return "auth:login:ip:" + address;
        }

        /// <summary>
        /// When the forwarded-headers middleware trusts the sender it consumes <c>X-Forwarded-For</c>, so a header
        /// still present means a proxy that isn't in <c>VX_TRUSTED_PROXIES</c>. Logged once per instance.
        /// </summary>
        private void WarnIfBehindUntrustedProxy(HttpContext context)
        {
            if (!context.Request.Headers.ContainsKey(ForwardedHeadersDefaults.XForwardedForHeaderName)) return;
            if (Interlocked.Exchange(ref _warnedUntrustedProxy, 1) == 1) return;

            _logger.LogWarning("Logins arrive through a proxy at {Address} that isn't trusted, so all of its clients share one address for login limits. " +
                               "Add it to {Variable} to limit each client separately.", context.Connection.RemoteIpAddress, ServerSettings.TrustedProxiesVariable);
        }

        private async Task<long[]> GetCountsAsync(params string[] keys)
        {
            long[]? shared = await _cache.GetCountersAsync(keys);
            if (shared is not null) return shared;

            DateTime now = DateTime.UtcNow;
            return keys.Select(k => _localFailures.TryGetValue(k, out Failures? f) && now - f.FirstAt < Window ? f.Count : 0).ToArray();
        }

        private async Task IncrementAsync(string key)
        {
            if (await _cache.IncrementCounterAsync(key, Window) is not null) return;

            DateTime now = DateTime.UtcNow;
            _localFailures.AddOrUpdate(key, _ => new Failures(1, now), (_, f) => now - f.FirstAt >= Window ? new Failures(1, now) : f with { Count = f.Count + 1 });

            // Drop expired entries now and then so guessed usernames don't accumulate.
            if (_localFailures.Count > 10_000)
            {
                foreach (KeyValuePair<string, Failures> entry in _localFailures)
                    if (now - entry.Value.FirstAt >= Window) _localFailures.TryRemove(entry);
            }
        }
    }
}
