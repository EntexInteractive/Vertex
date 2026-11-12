// Copyright 2026 Entex Interactive

using System.IO.Enumeration;
using Vertex.Core.Client.Workspaces;

namespace Vertex.Core.Client.IO
{
    /// <summary>
    /// A file found on disk, with the size and timestamp the directory listing reported for it.
    /// </summary>
    /// <param name="RelativePath">Path relative to the workspace root, with <c>/</c> separators.</param>
    /// <param name="Size">Length in bytes.</param>
    /// <param name="LastWriteTicks">Last write time, in UTC ticks.</param>
    public readonly record struct WorkingFile(string RelativePath, long Size, long LastWriteTicks);

    public static class FileSystemUtility
    {
        /// <summary>
        /// Checks if the given path is a directory.
        /// </summary>
        /// <returns>True if path is a directory, otherwise false.</returns>
        public static bool IsDirectory(string path)
        {
            return Directory.Exists(path) && !File.Exists(path);
        }

        /// <summary>
        /// Checks if the given path is a file.
        /// </summary>
        /// <returns>True if path is a file, otherwise false.</returns>
        public static bool IsFile(string path)
        {
            return !Directory.Exists(path) && File.Exists(path);
        }

        /// <summary>
        /// Lists every file under <paramref name="root"/>, skipping ignored directories, <c>.vx</c> folders and
        /// directory links (which could loop).
        /// </summary>
        /// <remarks>
        /// Size and timestamp come from the directory listing itself, so this makes one file system call per
        /// directory rather than one per file. Ignore patterns are only applied to directories here; whether an
        /// individual file is ignored depends on whether it is tracked, which the caller decides.
        /// </remarks>
        public static IEnumerable<WorkingFile> EnumerateFiles(string root, IgnoreRules ignore)
        {
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0, // The default skips hidden and system files, which still need versioning.
                ReturnSpecialDirectories = false
            };

            return new FileSystemEnumerable<WorkingFile>(root,
                (ref FileSystemEntry entry) => new WorkingFile(ToRelativePath(prefix, entry.ToFullPath()), entry.Length, entry.LastWriteTimeUtc.UtcTicks),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if (entry.FileName.Equals(Workspace.MetadataDirectoryName, StringComparison.OrdinalIgnoreCase)) return false;
                    return !ignore.IsIgnored(ToRelativePath(prefix, entry.ToFullPath()), isDirectory: true);
                }
            };
        }

        /// <summary>
        /// Converts a full path under <paramref name="rootPrefix"/> (the root ending in a separator) to a relative
        /// path with <c>/</c> separators.
        /// </summary>
        public static string ToRelativePath(string rootPrefix, string fullPath)
        {
            string relative = fullPath[rootPrefix.Length..];
            return Path.DirectorySeparatorChar == '/' ? relative : relative.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
