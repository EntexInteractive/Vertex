// Copyright 2026 Entex Interactive

namespace Vertex.Core.Storage
{
    /// <summary>
    /// Rules for file paths stored in a project: relative to the workspace root, with <c>/</c> separators, and valid on
    /// every platform a workspace may live on.
    /// </summary>
    public static class RepositoryPath
    {
        public const int MaximumLength = 1024;

        /// <summary>
        /// The key two paths are compared by. Game projects live on case-insensitive file systems (Windows, macOS), so
        /// paths that differ only in case are the same file.
        /// </summary>
        public static string ToKey(string path)
        {
            return path.ToLowerInvariant();
        }

        /// <summary>
        /// True for a relative path such as <c>Content/Maps/City.umap</c>: no leading or trailing <c>/</c>, no empty,
        /// <c>.</c> or <c>..</c> segments, no backslashes, colons or control characters.
        /// </summary>
        public static bool IsValid(string? path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > MaximumLength) return false;
            foreach (char c in path)
            {
                if (c is '\\' or ':' || char.IsControl(c)) return false;
            }

            foreach (string segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment is "." or "..") return false;
            }

            return true;
        }
    }
}
