// Copyright 2026 Entex Interactive

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Vertex.Core.Client.Auth
{
    /// <summary>
    /// A saved login for one server.
    /// </summary>
    public sealed record Credential(string Username, string Token);

    /// <summary>
    /// The tokens from <c>vx login</c>, one per server, in a single per-user file so every workspace (and later the
    /// agent) shares them, like Docker's <c>config.json</c>.
    /// </summary>
    /// <remarks>
    /// The file is <c>~/.vertex/credentials</c>, or <c>credentials</c> in <c>VX_CONFIG_DIR</c> when that is set. On
    /// Windows each token is encrypted with DPAPI for the current user; elsewhere the file is readable only by its
    /// owner (mode 600). Writes replace the file atomically, so a crash never leaves it half-written.
    /// </remarks>
    public static class CredentialStore
    {
        public const string ConfigDirectoryVariable = "VX_CONFIG_DIR";

        private const string DpapiProtection = "dpapi";
        private const string NoProtection = "none";
        private static readonly byte[] Entropy = "Vertex credentials"u8.ToArray();

        private sealed class Entry
        {
            public string Username { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
            public string Protection { get; set; } = NoProtection;
        }

        private sealed class CredentialsFile
        {
            public Dictionary<string, Entry> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The folder holding the credentials file.
        /// </summary>
        public static string Directory
        {
            get
            {
                string? overridden = Environment.GetEnvironmentVariable(ConfigDirectoryVariable);
                return string.IsNullOrWhiteSpace(overridden)
                    ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vertex")
                    : overridden;
            }
        }

        public static string Path => System.IO.Path.Combine(Directory, "credentials");

        /// <summary>
        /// Normalizes a server address so <c>HTTP://Host:5000/</c> and <c>http://host:5000</c> share one entry.
        /// </summary>
        public static string NormalizeServer(string server)
        {
            return Uri.TryCreate(server.Trim(), UriKind.Absolute, out Uri? uri)
                ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/')
                : server.Trim().TrimEnd('/').ToLowerInvariant();
        }

        /// <returns>The saved login for the server, or null when there is none or it can't be decrypted (e.g. the
        /// file was copied from another Windows account).</returns>
        public static Credential? Get(string server)
        {
            CredentialsFile file = Load();
            if (!file.Servers.TryGetValue(NormalizeServer(server), out Entry? entry)) return null;

            try
            {
                return new Credential(entry.Username, Unprotect(entry));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                Log.Debug(ex, "Could not decrypt the saved token for {Server}", server);
                return null;
            }
        }

        /// <summary>
        /// Saves a login, replacing any previous one for the server.
        /// </summary>
        public static void Save(string server, Credential credential)
        {
            CredentialsFile file = Load();
            Entry entry = new() { Username = credential.Username };
            if (OperatingSystem.IsWindows())
            {
                entry.Token = Convert.ToBase64String(ProtectWindows(credential.Token));
                entry.Protection = DpapiProtection;
            }
            else
            {
                entry.Token = credential.Token;
            }

            file.Servers[NormalizeServer(server)] = entry;
            Write(file);
        }

        /// <summary>
        /// Forgets the login for a server. Safe to call when there is none.
        /// </summary>
        /// <returns>True when a login was removed.</returns>
        public static bool Remove(string server)
        {
            CredentialsFile file = Load();
            if (!file.Servers.Remove(NormalizeServer(server))) return false;
            Write(file);
            return true;
        }

        private static CredentialsFile Load()
        {
            if (!File.Exists(Path)) return new CredentialsFile();

            try
            {
                CredentialsFile? file = JsonConvert.DeserializeObject<CredentialsFile>(File.ReadAllText(Path));
                if (file is null) return new CredentialsFile();

                // Deserializing replaces the dictionary, so restore case-insensitive keys.
                file.Servers = new Dictionary<string, Entry>(file.Servers, StringComparer.OrdinalIgnoreCase);
                return file;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"'{Path}' is not valid. Delete it and run 'vx login' again.", ex);
            }
        }

        private static void Write(CredentialsFile file)
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string temporary = Path + ".tmp";
            FileStreamOptions options = new() { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (StreamWriter writer = new(temporary, new UTF8Encoding(false), options))
            {
                writer.Write(JsonConvert.SerializeObject(file, Formatting.Indented));
            }

            File.Move(temporary, Path, true);
        }

        private static string Unprotect(Entry entry)
        {
            if (entry.Protection != DpapiProtection) return entry.Token;
            if (!OperatingSystem.IsWindows()) throw new CryptographicException("The token was encrypted on Windows.");
            return UnprotectWindows(Convert.FromBase64String(entry.Token));
        }

        [SupportedOSPlatform("windows")]
        private static byte[] ProtectWindows(string token)
        {
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        }

        [SupportedOSPlatform("windows")]
        private static string UnprotectWindows(byte[] data)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
        }
    }
}
