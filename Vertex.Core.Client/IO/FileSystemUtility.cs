// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using Vertex.Core.Client.Database;
using Vertex.Core.Client.Settings;
using Vertex.Core.Data;

namespace Vertex.Core.Client.IO
{
    public static class FileSystemUtility
    {
        #region Statics
        
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

        public static IEnumerable<string> GetFiles(string root)
        {
            return IsDirectory(root) ? GetFiles(root, GetIgnoredEntries(root)) : [root];
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

        public static string[] GetIgnoredEntries(string path)
        {
            string ignoreFile = Path.Combine(path, ".vxignore");
            if (!File.Exists(ignoreFile)) return [];
            
            string[] ignoredEntries = File.ReadAllLines(ignoreFile).Select(line => line.Trim()).Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')).ToArray();
            return File.Exists(ignoreFile) ? ignoredEntries : [];
        }

        /// <summary>
        /// Gets if a file has changed.
        /// </summary>
        /// <param name="source">The source file to compare.</param>
        /// <param name="target">The target file to compare to.</param>
        /// <returns>True is success, otherwise false.</returns>
        public static bool HasChanged(VFile source, VFile? target)
        {
            if (target is null || source.LastWrite.TotalMilliseconds <= target.LastWrite.TotalMilliseconds) return false;
            if (!source.TryChunkFile() || !target.TryChunkFile()) return false;
            return source.CheckSum != target.CheckSum;
        }

        #endregion

        //private readonly SqliteDatabase _database = new SqliteDatabase();

        public static async Task<IReadOnlyList<VChange>> GetChangesAsync(string path, Guid branchId, int maxConcurrency, bool force = false)
        {
            ConcurrentBag<string> scannedFiles = new();
            ConcurrentBag<VChange> changedFiles = new();

            HashSet<string> localFiles = IsDirectory(path) ? [.. GetFiles(path, GetIgnoredEntries(path))] : [path];
            IReadOnlyList<VFile> cachedFiles = await new SqliteDatabase().GetFilesAsync(path);

            Parallel.ForEach(cachedFiles, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency }, cachedFile =>
            {
                if (File.Exists(cachedFile.FullPath))
                {
                    VFile vxFile = new(path, cachedFile.FullPath, branchId);
                    if (IsIgnored(vxFile.FullPath, GetIgnoredEntries(path)))
                    {
                        //Log.Debug("Ignored: {VxFileRelativePath}", vxFile.RelativePath);
                        changedFiles.Add(new VChange(ChangeType.Deleted, vxFile));
                    }
                    else if (HasChanged(vxFile, cachedFile) || force)
                    {
                        //Log.Debug($"Changed -> {localFile.Fullname}");
                        changedFiles.Add(new VChange(ChangeType.Modified, vxFile));
                    }

                    scannedFiles.Add(cachedFile.FullPath);
                }
                else
                {
                    //Log.Debug($"Deleted -> {remoteFile.Fullname}");
                    changedFiles.Add(new VChange(ChangeType.Deleted, cachedFile));
                }
            });

            // Adds the remaining files as created
            IEnumerable<string> remainingFiles = localFiles.Except(scannedFiles);
            Parallel.ForEach(remainingFiles, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency }, remainingFile =>
            {
                if (IsIgnored(remainingFile, GetIgnoredEntries(path))) return;
                //Log.Debug($"Created -> {remainingFile}");
                changedFiles.Add(new VChange(ChangeType.Created, new VFile(path, remainingFile, branchId)));
            });

            return [.. changedFiles.OrderBy(x => x.File.RelativePath)];
        }
    }
}