// Copyright 2026 Entex Interactive

using System.Collections.Concurrent;
using Vertex.Core.Client.IO;
using Vertex.Core.Data;
using Vertex.Core.Storage;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// A difference between the workspace and what was last pushed or synced.
    /// </summary>
    /// <param name="Type">What changed.</param>
    /// <param name="Path">Relative path with <c>/</c> separators. For a move, the new path.</param>
    /// <param name="FileId">Content id of the working file; for a deletion, the content id that was deleted.</param>
    /// <param name="Size">Size of the working file; for a deletion, its last known size.</param>
    /// <param name="PreviousPath">For a move, the old path.</param>
    public sealed record WorkspaceChange(ChangeType Type, string Path, string FileId, long Size, string? PreviousPath = null)
    {
        /// <summary>
        /// The file's chunks, when it was hashed during this scan and <see cref="ScanOptions.KeepChunks"/> was set.
        /// </summary>
        public ChunkedFile? Chunks { get; init; }
    }

    public sealed class ScanOptions
    {
        public int MaxConcurrency { get; init; } = Environment.ProcessorCount;

        /// <summary>Re-hash every file instead of trusting size and timestamp.</summary>
        public bool Force { get; init; }

        /// <summary>Keep the chunk lists of hashed files on the changes, for pushing them.</summary>
        public bool KeepChunks { get; init; }
    }

    /// <summary>
    /// The result of <see cref="WorkspaceScanner.ScanPaths"/>: which of the checked paths are changed, which are not,
    /// and which couldn't be read (e.g. still being written).
    /// </summary>
    public sealed record PathScan(IReadOnlyList<WorkspaceChange> Changed, IReadOnlyList<string> Unchanged, IReadOnlyList<string> Unreadable);

    public sealed class WorkspaceStatus
    {
        /// <summary>Changes, ordered by path.</summary>
        public required IReadOnlyList<WorkspaceChange> Changes { get; init; }

        /// <summary>The index after the scan, keyed by relative path.</summary>
        public required IReadOnlyDictionary<string, IndexEntry> Entries { get; init; }

        /// <summary>Files that could not be read (locked or no access). They are left out of <see cref="Changes"/>.</summary>
        public required IReadOnlyList<string> Unreadable { get; init; }

        public int FilesScanned { get; init; }
        public int FilesHashed { get; init; }
        public long BytesHashed { get; init; }
    }

    /// <summary>
    /// Compares a workspace with its index to find what changed since the last push or sync.
    /// </summary>
    /// <remarks>
    /// A file whose size and timestamp match the index is trusted to be unchanged and is not read, so a scan with
    /// nothing changed costs one directory listing. Anything else is hashed, and the index is updated so the next
    /// scan can skip it. The comparison is always by content id, so touching a file without editing it, or
    /// editing it and putting it back, reports no change.
    /// </remarks>
    public static class WorkspaceScanner
    {
        /// <summary>
        /// A file modified this close to when it was hashed may have changed again within the file system's
        /// timestamp resolution (2 s on FAT, coarse on some network shares), so it is hashed again.
        /// </summary>
        private static readonly long RacyWindowTicks = TimeSpan.FromSeconds(2).Ticks;

        /// <summary>Path comparison matching the platform's file system.</summary>
        public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static WorkspaceStatus Scan(VWorkspace workspace, WorkspaceIndex index, ScanOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= new ScanOptions();
            IgnoreRules ignore = IgnoreRules.Load(workspace.Root);

            Dictionary<string, IndexEntry> indexed = new(PathComparer);
            foreach (IndexEntry entry in index.ReadAll()) indexed.TryAdd(entry.Path, entry);

            // Files on disk, plus tracked files the listing skipped because they sit in an ignored directory.
            List<WorkingFile> working = FileSystemUtility.EnumerateFiles(workspace.Root, ignore).ToList();
            HashSet<string> seen = new(working.Select(f => f.RelativePath), PathComparer);

            List<WorkspaceChange> deleted = new();
            List<string> staleRows = new();
            foreach (IndexEntry entry in indexed.Values)
            {
                if (seen.Contains(entry.Path)) continue;

                FileInfo file = new(workspace.ToFullPath(entry.Path));
                if (entry.BaseId is null)
                {
                    if (!file.Exists) staleRows.Add(entry.Path); // Untracked and gone: just forget it.
                }
                else if (file.Exists)
                {
                    working.Add(new WorkingFile(entry.Path, file.Length, file.LastWriteTimeUtc.Ticks));
                }
                else
                {
                    deleted.Add(new WorkspaceChange(ChangeType.Deleted, entry.Path, entry.BaseId, entry.Size));
                }
            }

            ConcurrentBag<WorkspaceChange> changes = new();
            ConcurrentBag<IndexEntry> updated = new();
            ConcurrentBag<string> unreadable = new();
            int hashed = 0;
            long bytesHashed = 0;

            ParallelOptions parallel = new() { MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency), CancellationToken = cancellationToken };
            Parallel.ForEach(working, parallel, file =>
            {
                indexed.TryGetValue(file.RelativePath, out IndexEntry? entry);
                FileCheck check = CheckFile(workspace, file, entry, ignore, options, cancellationToken);
                if (check.Unreadable) unreadable.Add(file.RelativePath);
                if (check.Updated is not null)
                {
                    updated.Add(check.Updated);
                    Interlocked.Increment(ref hashed);
                    Interlocked.Add(ref bytesHashed, check.Updated.Size);
                }

                if (check.Change is not null) changes.Add(check.Change);
            });

            List<WorkspaceChange> all = DetectMoves(changes.Concat(deleted));
            index.Apply(updated, staleRows);

            foreach (IndexEntry entry in updated) indexed[entry.Path] = entry;
            foreach (string path in staleRows) indexed.Remove(path);

            return new WorkspaceStatus
            {
                Changes = all.OrderBy(c => c.Path, PathComparer).ToList(),
                Entries = indexed,
                Unreadable = unreadable.OrderBy(p => p, PathComparer).ToList(),
                FilesScanned = working.Count,
                FilesHashed = hashed,
                BytesHashed = bytesHashed
            };
        }

        /// <summary>
        /// Checks only some paths instead of the whole workspace, for a file watcher: each path is re-checked (and
        /// re-hashed only if its size or timestamp moved), a folder stands for every file in it, and a path that no
        /// longer exists counts as deleted when it was tracked. Moves show up as a deletion plus a creation.
        /// </summary>
        /// <param name="paths">Relative paths with <c>/</c> separators, of files or folders.</param>
        /// <param name="hash">False to skip hashing: a file whose size or timestamp moved counts as changed without
        /// reading it (its change has an empty <see cref="WorkspaceChange.FileId"/>), and the index isn't updated for
        /// it. That's enough to lock it at once; a hashing pass afterwards tells real changes from touched files.</param>
        public static PathScan ScanPaths(VWorkspace workspace, WorkspaceIndex index, IReadOnlyCollection<string> paths, bool hash = true, CancellationToken cancellationToken = default)
        {
            IgnoreRules ignore = IgnoreRules.Load(workspace.Root);
            ScanOptions options = new();

            // Expand folders into their files; anything gone may have been a file or a whole folder.
            Dictionary<string, WorkingFile> working = new(PathComparer);
            List<string> missing = new();
            foreach (string path in paths)
            {
                // The workspace's own metadata is never part of it.
                string first = path.Split('/', 2)[0];
                if (first.Equals(VWorkspace.MetadataDirectoryName, StringComparison.OrdinalIgnoreCase)) continue;

                string fullPath = workspace.ToFullPath(path);
                FileInfo file = new(fullPath);
                if (file.Exists)
                {
                    working[path] = new WorkingFile(path, file.Length, file.LastWriteTimeUtc.Ticks);
                }
                else if (Directory.Exists(fullPath))
                {
                    if (ignore.IsIgnored(path, isDirectory: true) || InIgnoredFolder(ignore, path)) continue;
                    foreach (WorkingFile inside in FileSystemUtility.EnumerateFiles(fullPath, IgnoreRules.Empty))
                    {
                        string relative = path + "/" + inside.RelativePath;
                        working[relative] = inside with { RelativePath = relative };
                    }

                    missing.Add(path); // Tracked files under a folder that was replaced may be gone.
                }
                else
                {
                    missing.Add(path);
                }
            }

            Dictionary<string, IndexEntry> indexed = new(PathComparer);
            foreach (IndexEntry entry in index.Find(working.Keys.Concat(missing), missing)) indexed.TryAdd(entry.Path, entry);

            // Hash in parallel like the full scan: a burst of new files (an editor saving a folder) is all reads.
            ConcurrentBag<WorkspaceChange> changedBag = new();
            ConcurrentBag<string> unchangedBag = new();
            ConcurrentBag<string> unreadableBag = new();
            ConcurrentBag<IndexEntry> updatedBag = new();
            ParallelOptions parallel = new() { MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency), CancellationToken = cancellationToken };
            Parallel.ForEach(working.Values, parallel, file =>
            {
                indexed.TryGetValue(file.RelativePath, out IndexEntry? entry);

                // The full scan never lists ignored folders; untracked files inside them aren't the workspace's.
                if (entry?.BaseId is null && InIgnoredFolder(ignore, file.RelativePath))
                {
                    unchangedBag.Add(file.RelativePath);
                    return;
                }

                FileCheck check = CheckFile(workspace, file, entry, ignore, options, cancellationToken, hash);
                if (check.Updated is not null) updatedBag.Add(check.Updated);
                if (check.Unreadable) unreadableBag.Add(file.RelativePath);
                else if (check.Change is not null) changedBag.Add(check.Change);
                else unchangedBag.Add(entry?.Path ?? file.RelativePath);
            });

            List<WorkspaceChange> changed = changedBag.ToList();
            List<string> unchanged = unchangedBag.ToList();
            List<string> unreadable = unreadableBag.ToList();
            List<IndexEntry> updated = updatedBag.ToList();
            List<string> staleRows = new();

            foreach (IndexEntry entry in indexed.Values)
            {
                if (working.ContainsKey(entry.Path) || File.Exists(workspace.ToFullPath(entry.Path))) continue;
                if (entry.BaseId is null)
                {
                    staleRows.Add(entry.Path);
                    unchanged.Add(entry.Path);
                }
                else
                {
                    changed.Add(new WorkspaceChange(ChangeType.Deleted, entry.Path, entry.BaseId, entry.Size));
                }
            }

            // A path that is gone and was never indexed (a temporary file) is simply unchanged.
            foreach (string path in missing)
            {
                if (!indexed.ContainsKey(path) && !Directory.Exists(workspace.ToFullPath(path))) unchanged.Add(path);
            }

            index.Apply(updated, staleRows);
            return new PathScan(changed, unchanged, unreadable);
        }

        /// <summary>
        /// True when a folder above <paramref name="path"/> is ignored, e.g. <c>Intermediate/Build/x.obj</c> with
        /// <c>Intermediate/</c> in <c>.vxignore</c>.
        /// </summary>
        private static bool InIgnoredFolder(IgnoreRules ignore, string path)
        {
            for (int slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                if (ignore.IsIgnored(path[..slash], isDirectory: true)) return true;
            }

            return false;
        }

        /// <summary>
        /// One file's state against its index row: its change (if any), the updated row when it was hashed, or that it
        /// couldn't be read.
        /// </summary>
        private sealed record FileCheck(WorkspaceChange? Change, IndexEntry? Updated, bool Unreadable);

        private static readonly FileCheck Skipped = new(null, null, false);

        private static FileCheck CheckFile(VWorkspace workspace, WorkingFile file, IndexEntry? entry, IgnoreRules ignore, ScanOptions options, CancellationToken cancellationToken,
            bool hash = true)
        {
            // Ignore rules only apply to untracked files.
            if (entry?.BaseId is null && ignore.IsIgnored(file.RelativePath, isDirectory: false)) return Skipped;

            string fileId;
            ChunkedFile? chunks = null;
            IndexEntry? updated = null;
            bool cached = entry is not null && !options.Force
                                            && entry.Size == file.Size && entry.LastWriteTicks == file.LastWriteTicks
                                            && file.LastWriteTicks < entry.IndexedAtTicks - RacyWindowTicks;

            if (cached)
            {
                fileId = entry!.FileId;
            }
            else if (!hash)
            {
                // Its stats moved, so it may have changed; don't read it yet.
                return new FileCheck(new WorkspaceChange(entry?.BaseId is null ? ChangeType.Created : ChangeType.Modified, file.RelativePath, string.Empty, file.Size), null, false);
            }
            else
            {
                long now = DateTime.UtcNow.Ticks;
                try
                {
                    chunks = ContentChunker.ChunkFile(workspace.ToFullPath(file.RelativePath), cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Could not read {Path}", file.RelativePath);
                    return new FileCheck(null, null, true);
                }

                fileId = chunks.FileId;

                // Keep the path as the index spells it, so a case-only difference on Windows doesn't add a row.
                updated = new IndexEntry(entry?.Path ?? file.RelativePath, file.Size, file.LastWriteTicks, fileId, entry?.BaseId, now);
            }

            ChangeType? type = entry?.BaseId switch
            {
                null => ChangeType.Created,
                string baseId when baseId != fileId => ChangeType.Modified,
                _ => null
            };

            WorkspaceChange? change = type is null ? null : new WorkspaceChange(type.Value, file.RelativePath, fileId, file.Size) { Chunks = options.KeepChunks ? chunks : null };
            return new FileCheck(change, updated, false);
        }

        /// <summary>
        /// Pairs each deleted file with a created file of the same content and reports them as one move.
        /// </summary>
        private static List<WorkspaceChange> DetectMoves(IEnumerable<WorkspaceChange> changes)
        {
            List<WorkspaceChange> result = new();
            Dictionary<string, Queue<WorkspaceChange>> deletedByContent = new();
            List<WorkspaceChange> created = new();

            foreach (WorkspaceChange change in changes)
            {
                // Empty files all share one content id, so pairing them would invent moves.
                if (change.Size > 0 && change.Type == ChangeType.Deleted)
                {
                    if (!deletedByContent.TryGetValue(change.FileId, out Queue<WorkspaceChange>? queue))
                        deletedByContent[change.FileId] = queue = new Queue<WorkspaceChange>();

                    queue.Enqueue(change);
                }
                else if (change.Size > 0 && change.Type == ChangeType.Created)
                {
                    created.Add(change);
                }
                else
                {
                    result.Add(change);
                }
            }

            // Sort so the pairing is the same on every run.
            foreach (WorkspaceChange change in created.OrderBy(c => c.Path, StringComparer.Ordinal))
            {
                if (deletedByContent.TryGetValue(change.FileId, out Queue<WorkspaceChange>? queue) && queue.TryDequeue(out WorkspaceChange? source))
                {
                    result.Add(change with { Type = ChangeType.Renamed, PreviousPath = source.Path });
                }
                else
                {
                    result.Add(change);
                }
            }

            result.AddRange(deletedByContent.Values.SelectMany(q => q));
            return result;
        }
    }
}