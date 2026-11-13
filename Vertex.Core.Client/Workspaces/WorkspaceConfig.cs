// Copyright 2026 Entex Interactive

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// A workspace's settings, stored in <c>.vx/config</c> as <c>Key=Value</c> lines.
    /// </summary>
    public sealed class WorkspaceConfig
    {
        public string Root => $"{Project}/{Branch}";
        
        public string? Address { get; init; }
        public Guid? ProjectId { get; init; }
        public Guid? BranchId { get; init; }
        public string? Project { get; init; }
        public string? Branch { get; init; }
        public string? User { get; init; }

        /// <summary>
        /// Reads a config file. Blank lines and lines starting with <c>#</c> or <c>;</c> are skipped, keys are
        /// case-insensitive, values may be quoted, and the last value for a key wins.
        /// </summary>
        /// <returns>The config, or null when the file does not exist.</returns>
        public static WorkspaceConfig? Load(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            if (!File.Exists(path)) return null;

            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

                // Split on the first '=' only, so values containing '=' survive.
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                    value = value[1..^1];

                values[key] = value;
            }

            return new WorkspaceConfig
            {
                Address = Get(values, "Address"),
                ProjectId = Guid.TryParse(Get(values, "ProjectId"), out Guid projectId) ? projectId : null,
                BranchId = Guid.TryParse(Get(values, "BranchId"), out Guid branchId) ? branchId : null,
                Project = Get(values, "Project"),
                Branch = Get(values, "Branch"),
                User = Get(values, "User")
            };
        }

        /// <summary>
        /// Writes the config in the format <see cref="Load"/> reads, skipping unset values.
        /// </summary>
        public void Save(string path)
        {
            List<string> lines = ["# Vertex workspace configuration"];
            if (Address is not null) lines.Add($"Address={Address}");
            if (ProjectId is not null) lines.Add($"ProjectId={ProjectId}");
            if (BranchId is not null) lines.Add($"BranchId={BranchId}");
            if (Project is not null) lines.Add($"Project={Project}");
            if (Branch is not null) lines.Add($"Branch={Branch}");
            if (User is not null) lines.Add($"User={User}");
            File.WriteAllLines(path, lines);
        }

        private static string? Get(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out string? v) && v.Length > 0 ? v : null;
        }
    }
}