// Copyright 2026 Entex Interactive

namespace Vertex.Core.Client.Data
{
    public class VConfig
    {
        public string? Address { get; init; }
        public string? Project { get; init; }
        public string? Branch { get; init; }
        public string? User { get; init; }


        public static VConfig? Parse(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            if (!File.Exists(path)) return null;

            IEnumerable<string> lines = File.ReadAllLines(path);
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                // Skip blank lines and comments
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                    continue;

                // Split on the FIRST '=' only, so values like URLs with '=' in them survive
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue; // no '=' or empty key -> ignore (or throw if you prefer strict)

                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();

                // Strip optional surrounding quotes: Project="My Project"
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[^1] == '"') ||
                     (value[0] == '\'' && value[^1] == '\'')))
                {
                    value = value[1..^1];
                }

                values[key] = value; // last one wins if duplicated
            }

            return new VConfig
            {
                Address = Get(values, "Address"),
                Project = Get(values, "Project"),
                Branch = Get(values, "Branch"),
                User = Get(values, "User"),
            };
        }

        /// <summary>
        /// Parses a <see cref="VConfig"/> instance from a configuration file located at the specified path relative to the root directory.
        /// </summary>
        /// <param name="relativePath">The directory path used to locate the .vxconfig configuration file.</param>
        /// <return>The parsed <see cref="VConfig"/> instance if the configuration file is found and successfully parsed; otherwise, <see langword="null"/>.</return>
        public static VConfig? ParseFromRoot(string relativePath)
        {
            return Parse(Path.Combine(relativePath, ".vxconfig"));
        }

        private static string? Get(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out string? v) && v.Length > 0 ? v : null;
        }
    }
}