// Copyright 2026 Entex Interactive

using Vertex.Core.Client.Auth;
using Vertex.Core.Client.IO;

namespace Vertex.Core.Client.Config
{
    /// <summary>
    /// Per-user settings that apply to every command on this machine, set with <c>vx config</c>, like Perforce's
    /// <c>p4 set</c>. Stored as <c>config</c> next to the credentials file (<c>~/.vertex</c>, or
    /// <c>VX_CONFIG_DIR</c>).
    /// </summary>
    public sealed class UserConfig
    {
        /// <summary>
        /// The server used when neither <c>--server</c> nor the current workspace names one.
        /// </summary>
        public string? Server { get; set; }

        public static string Path => System.IO.Path.Combine(CredentialStore.Directory, "config");

        /// <summary>
        /// Reads the settings. A missing or unreadable file reads as no settings, so a broken file never stops a
        /// command; <c>vx config</c> rewrites it.
        /// </summary>
        public static UserConfig Load()
        {
            try
            {
                return File.Exists(Path) ? JsonConvert.DeserializeObject<UserConfig>(File.ReadAllText(Path)) ?? new UserConfig() : new UserConfig();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Could not read {Path}", Path);
                return new UserConfig();
            }
        }

        public void Save()
        {
            AtomicFile.WriteAllText(Path, JsonConvert.SerializeObject(this, Formatting.Indented), false);
        }
    }
}
