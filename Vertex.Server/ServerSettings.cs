// Copyright 2026 Entex Interactive

using System.Net;
using System.Net.Sockets;

namespace Vertex.Server
{
    /// <summary>
    /// Server configuration, read from environment variables at startup. Every setting the server needs is
    /// listed here, so this class is the single reference for deployment configuration.
    /// </summary>
    /// <remarks>
    /// When <see cref="StorageSettings.AccessKey"/> and <see cref="StorageSettings.SecretKey"/> are unset, the AWS SDK
    /// falls back to its default credential chain (AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY, web identity such as
    /// EKS IRSA or pod identity, instance profile).
    /// </remarks>
    public sealed class ServerSettings
    {
        public const string HttpPortVariable = "VX_HTTP_PORT";
        public const string PostgresVariable = "VX_POSTGRES_CONNECTION";
        public const string RedisVariable = "VX_REDIS_CONNECTION";
        public const string S3EndpointVariable = "VX_S3_ENDPOINT";
        public const string S3RegionVariable = "VX_S3_REGION";
        public const string S3BucketVariable = "VX_S3_BUCKET";
        public const string S3AccessKeyVariable = "VX_S3_ACCESS_KEY";
        public const string S3SecretKeyVariable = "VX_S3_SECRET_KEY";
        public const string TlsCertificateVariable = "VX_TLS_CERT";
        public const string TlsKeyVariable = "VX_TLS_KEY";
        public const string AdminPasswordVariable = "VX_ADMIN_PASSWORD";
        public const string TrustedProxiesVariable = "VX_TRUSTED_PROXIES";

        /// <summary>Port Kestrel listens on. Default 5000.</summary>
        public required int HttpPort { get; init; }

        /// <summary>
        /// PEM certificate and private key files. When set, the port uses TLS and serves both HTTP/1.1 and HTTP/2
        /// (negotiated with ALPN), which proxies such as Cloudflare need. When unset, the port serves clear-text
        /// HTTP/2 only, for local development and traffic inside a cluster.
        /// </summary>
        public string? TlsCertificatePath { get; init; }

        /// <inheritdoc cref="TlsCertificatePath"/>
        public string? TlsKeyPath { get; init; }

        public bool UseTls => TlsCertificatePath is not null;

        /// <summary>Npgsql connection string. Required.</summary>
        public required string PostgresConnectionString { get; init; }

        /// <summary>StackExchange.Redis configuration string. Default <c>redis:6379</c>.</summary>
        public required string RedisConnectionString { get; init; }

        public required StorageSettings Storage { get; init; }

        /// <summary>
        /// Password for the <c>admin</c> account the server creates on first start, while no users exist. Ignored
        /// once any user exists, so it can stay set or be removed after the first start.
        /// </summary>
        public string? AdminPassword { get; init; }

        /// <summary>
        /// Proxies whose <c>X-Forwarded-For</c> header is trusted for the client's address, as addresses or CIDR
        /// ranges. Loopback is always trusted. Set it when the server sits behind a proxy or tunnel (for example
        /// Cloudflare Tunnel in another container); otherwise every client appears to come from the proxy, and they
        /// share one login limit.
        /// </summary>
        public IReadOnlyList<IPNetwork> TrustedProxies { get; init; } = [];

        /// <summary>
        /// Reads and validates all settings from the environment.
        /// </summary>
        /// <exception cref="InvalidOperationException">One or more settings are missing or invalid; the message lists all of them.</exception>
        public static ServerSettings FromEnvironment()
        {
            List<string> errors = new();

            string portText = Read(HttpPortVariable) ?? "5000";
            if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
                errors.Add($"{HttpPortVariable} must be a port number between 1 and 65535 (got '{portText}').");

            string? postgres = Read(PostgresVariable);
            if (postgres is null)
                errors.Add($"{PostgresVariable} is required, e.g. 'Host=postgres;Port=5432;Database=vertex;Username=...;Password=...'.");

            string endpoint = Read(S3EndpointVariable) ?? "https://s3.amazonaws.com";
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
                errors.Add($"{S3EndpointVariable} must be an absolute URL (got '{endpoint}').");

            string? accessKey = Read(S3AccessKeyVariable);
            string? secretKey = Read(S3SecretKeyVariable);
            if (accessKey is null != secretKey is null)
                errors.Add($"{S3AccessKeyVariable} and {S3SecretKeyVariable} must either both be set or both be unset.");

            string? tlsCertificate = Read(TlsCertificateVariable);
            string? tlsKey = Read(TlsKeyVariable);
            if (tlsCertificate is null != tlsKey is null)
                errors.Add($"{TlsCertificateVariable} and {TlsKeyVariable} must either both be set or both be unset.");

            if (tlsCertificate is not null && !File.Exists(tlsCertificate))
                errors.Add($"{TlsCertificateVariable} points to '{tlsCertificate}', which does not exist.");

            if (tlsKey is not null && !File.Exists(tlsKey))
                errors.Add($"{TlsKeyVariable} points to '{tlsKey}', which does not exist.");

            string? adminPassword = Environment.GetEnvironmentVariable(AdminPasswordVariable);
            if (string.IsNullOrEmpty(adminPassword)) adminPassword = null;
            if (adminPassword is not null && adminPassword.Length < Auth.PasswordHasher.MinimumLength)
                errors.Add($"{AdminPasswordVariable} must be at least {Auth.PasswordHasher.MinimumLength} characters.");

            List<IPNetwork> trustedProxies = new();
            foreach (string entry in (Read(TrustedProxiesVariable) ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseNetwork(entry, out IPNetwork network)) trustedProxies.Add(network);
                else errors.Add($"{TrustedProxiesVariable} has '{entry}', which isn't an IP address or CIDR range such as '172.18.0.0/16'.");
            }

            if (errors.Count > 0)
                throw new InvalidOperationException("Invalid server configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));

            return new ServerSettings
            {
                HttpPort = port,
                TlsCertificatePath = tlsCertificate,
                TlsKeyPath = tlsKey,
                PostgresConnectionString = postgres!,
                RedisConnectionString = Read(RedisVariable) ?? "redis:6379",
                AdminPassword = adminPassword,
                TrustedProxies = trustedProxies,
                Storage = new StorageSettings
                {
                    Endpoint = endpoint,
                    Region = Read(S3RegionVariable) ?? "us-east-1",
                    Bucket = Read(S3BucketVariable) ?? "vertex-storage",
                    AccessKey = accessKey,
                    SecretKey = secretKey
                }
            };
        }

        /// <summary>
        /// Parses a CIDR range, or a single address as a range holding just that address.
        /// </summary>
        private static bool TryParseNetwork(string text, out IPNetwork network)
        {
            if (text.Contains('/')) return IPNetwork.TryParse(text, out network);
            if (IPAddress.TryParse(text, out IPAddress? address))
            {
                network = new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
                return true;
            }

            network = default;
            return false;
        }

        /// <summary>
        /// Returns the variable's value, treating unset and blank the same.
        /// </summary>
        private static string? Read(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class StorageSettings
    {
        /// <summary>S3-compatible endpoint URL. Default <c>https://s3.amazonaws.com</c>.</summary>
        public required string Endpoint { get; init; }

        /// <summary>Default <c>us-east-1</c>.</summary>
        public required string Region { get; init; }

        /// <summary>Default <c>vertex-storage</c>.</summary>
        public required string Bucket { get; init; }

        /// <summary>Optional; unset uses the AWS default credential chain.</summary>
        public string? AccessKey { get; init; }

        public string? SecretKey { get; init; }
    }
}
