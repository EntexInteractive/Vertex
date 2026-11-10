// Copyright 2026 Entex Interactive

using Vertex.Core.Data;

namespace Vertex.Core.IO
{
    public class FileSystemUtility
    {
        /// <summary>
        /// Checks if the given path is a directory.
        /// </summary>
        /// <param name="path"></param>
        /// <returns>True if path is a directory, otherwise false.</returns>
        public static bool IsDirectory(string path)
        {
            return Directory.Exists(path) && !File.Exists(path);
        }

        /// <summary>
        /// Checks if the given path is a file.
        /// </summary>
        /// <param name="path"></param>
        /// <returns>True if path is a file, otherwise false.</returns>
        public static bool IsFile(string path)
        {
            return !Directory.Exists(path) && File.Exists(path);
        }

        public static IEnumerable<string> GetFiles(string root, string searchPattern)
        {
            return GetFiles(root, [], searchPattern);
        }

        public static IEnumerable<string> GetFiles(string root, string[] exempt, string searchPattern = "*", bool recursive = true)
        {
            Stack<string> pending = new();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (IsIgnored(current, exempt))
                {
                    Log.Debug($"Ignored -> {current}");
                    continue;
                }

                // Get files in current directory
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(current, searchPattern);
                }
                catch
                {
                    Log.Warning($"No file access -> {current}");
                    continue;
                }

                foreach (string file in files)
                {
                    // This is a better system. However, if a user adds something to ignore, with this code it will never mark it for deletion.
                    // if (IsIgnored(file, exempt))
                    // {
                    //     Log.Debug($"Ignored -> {file}");
                    //     continue;
                    // }

                    yield return file;
                }

                if (!recursive) continue;
                IEnumerable<string> subDirs;
                try
                {
                    subDirs = Directory.EnumerateDirectories(current);
                }
                catch
                {
                    Log.Warning($"No directory access -> {current}");
                    continue;
                }

                foreach (string dir in subDirs) pending.Push(dir);
            }
        }

        /// <summary>
        /// Checks if the given path is set to be ignored.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="exempt"></param>
        /// <returns>True if ignored, otherwise false.</returns>
        public static bool IsIgnored(string path, string[] exempt)
        {
            string[] dirs = path.Split(Path.DirectorySeparatorChar);
            string fileName = Path.GetFileName(path);
            string extension = Path.GetExtension(path);

            foreach (string entry in exempt)
            {
                if (path.StartsWith(entry, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (entry.EndsWith('/'))
                {
                    string dirName = entry.TrimEnd('/');
                    if (dirs.Any(d => d.Equals(dirName, StringComparison.OrdinalIgnoreCase))) return true;
                }
                else if (entry.StartsWith("*"))
                {
                    if (extension.Equals(entry.Substring(1), StringComparison.OrdinalIgnoreCase)) return true;
                }
                else
                {
                    if (fileName.Equals(entry, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        public IReadOnlyList<VChange> GetFileChanges(VBranch branch)
        {
            List<VChange> changes = new();
            //IReadOnlyList<VFile> localFiles = GetFiles(branch);

            return changes;
        }

        public static string[] GetIgnoredEntries(string path)
        {
            string ignoreFile = Path.Combine(path, ".vxignore");
            string[] ignoredEntries = File.ReadAllLines(ignoreFile).Select(line => line.Trim()).Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')).ToArray();
            return File.Exists(ignoreFile) ? ignoredEntries : [];
        }
    }
}