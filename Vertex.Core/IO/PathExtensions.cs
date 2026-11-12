// Copyright 2026 Entex Interactive

namespace Vertex.Core.IO
{
    public static class PathExtensions
    {
        /// <summary>
        /// Converts backslashes in a project-relative path to forward slashes for storage.
        /// </summary>
        public static string NormalizePath(this string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            return path.Replace('\\', '/');
        }

        public static string ToRelativePath(this string fullPath, string rootPath)
        {
            return Path.GetRelativePath(rootPath, fullPath).NormalizePath();
        }

        public static string ToAbsolutePath(this string relativePath, string rootPath)
        {
            return Path.GetFullPath(Path.Combine(rootPath, relativePath));
        }
    }
}