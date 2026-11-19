// Copyright 2026 Entex Interactive

using System.Security.Cryptography;
using System.Text;

namespace Vertex.Gui.Hosting
{
    /// <summary>
    /// What the window needs to reach the local API and what the API checks on every call: the per-launch token, the
    /// port Kestrel bound and the origins the UI may be loaded from.
    /// </summary>
    public sealed class HostIdentity
    {
        public const string TokenHeader = "X-Vertex-Token";

        private readonly byte[] _tokenBytes;
        private readonly HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);

        public HostIdentity() : this(null)
        {
        }

        /// <param name="token">A fixed token for debug runs, so scripts and test browsers can call the API. Null
        /// generates a random one, which is what every normal launch does.</param>
        public HostIdentity(string? token)
        {
            Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _tokenBytes = Encoding.ASCII.GetBytes(Token);
        }

        /// <summary>
        /// A random 256-bit secret, new for every launch. The window hands it to the page in the URL fragment, which
        /// is never sent over the wire, and the page sends it back in <see cref="TokenHeader"/>.
        /// </summary>
        public string Token { get; }

        /// <summary>
        /// The loopback port Kestrel listens on. Zero until the server has started.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// The address the embedded UI is served from, e.g. <c>http://127.0.0.1:51234</c>.
        /// </summary>
        public string Origin => $"http://127.0.0.1:{Port}";

        public void SetPort(int port)
        {
            Port = port;
            _allowedOrigins.Add($"http://127.0.0.1:{port}");
            _allowedOrigins.Add($"http://localhost:{port}");
        }

        /// <summary>
        /// Also accepts calls from a page served elsewhere, i.e. the Vite dev server proxying <c>/api</c> to us.
        /// </summary>
        public void AllowOrigin(string origin)
        {
            _allowedOrigins.Add(origin.TrimEnd('/'));
        }

        public bool IsAllowedHost(string host)
        {
            return string.Equals(host, $"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(host, $"localhost:{Port}", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsAllowedOrigin(string origin)
        {
            return _allowedOrigins.Contains(origin);
        }

        public bool IsValidToken(string? token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            byte[] given = Encoding.ASCII.GetBytes(token);
            return CryptographicOperations.FixedTimeEquals(given, _tokenBytes);
        }
    }
}
