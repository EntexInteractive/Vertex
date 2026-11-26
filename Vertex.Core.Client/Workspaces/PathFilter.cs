// Copyright 2026 Entex Interactive

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// A set of files and folders in a workspace, as workspace-relative paths. Used to pick which changes an operation
    /// works on.
    /// </summary>
    public sealed class PathFilter
    {
        private readonly List<string> _prefixes;

        /// <summary>Paths from <see cref="FromRelative"/>, looked up per ancestor so huge selections stay linear.</summary>
        private readonly HashSet<string>? _paths;

        private PathFilter(List<string> prefixes)
        {
            _prefixes = prefixes;
        }

        private PathFilter(HashSet<string> paths)
        {
            _prefixes = [];
            _paths = paths;
        }

        /// <summary>
        /// Every file in the workspace.
        /// </summary>
        public static PathFilter All { get; } = new(new List<string> { string.Empty });

        /// <summary>
        /// The files and folders named on a command line, resolved against the current folder. No paths means the
        /// current folder.
        /// </summary>
        /// <exception cref="InvalidOperationException">A path is outside the workspace.</exception>
        public static PathFilter Create(VWorkspace workspace, IReadOnlyCollection<string> paths)
        {
            List<string> prefixes = new();
            foreach (string path in paths.Count > 0 ? paths : ["."])
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                string root = Path.TrimEndingDirectorySeparator(workspace.Root);
                if (WorkspaceScanner.PathComparer.Equals(full, root))
                {
                    prefixes.Add(string.Empty);
                    continue;
                }

                bool inside = full.Length > root.Length + 1 && Path.IsPathRooted(full)
                                                            && WorkspaceScanner.PathComparer.Equals(full[..(root.Length + 1)], root + Path.DirectorySeparatorChar);
                if (!inside) throw new InvalidOperationException($"'{path}' is outside the workspace '{workspace.Root}'.");

                prefixes.Add(workspace.ToRelativePath(full));
            }

            return new PathFilter(prefixes);
        }

        /// <summary>
        /// Workspace-relative paths (with '/' separators) as a host that lists changes already has them. An empty
        /// list matches nothing.
        /// </summary>
        public static PathFilter FromRelative(IEnumerable<string> paths)
        {
            HashSet<string> set = new(WorkspaceScanner.PathComparer);
            foreach (string path in paths)
            {
                string normalized = path.Replace('\\', '/').Trim('/');
                if (normalized.Length > 0) set.Add(normalized);
            }

            return new PathFilter(set);
        }

        /// <summary>
        /// True when <paramref name="path"/> is one of the named files or inside one of the named folders.
        /// </summary>
        public bool Matches(string? path)
        {
            if (path is null) return false;
            if (_paths is not null)
            {
                // The path itself, or any folder above it.
                for (int end = path.Length; end > 0; end = path.LastIndexOf('/', end - 1))
                {
                    if (_paths.Contains(end == path.Length ? path : path[..end])) return true;
                }

                return false;
            }

            StringComparison comparison = ReferenceEquals(WorkspaceScanner.PathComparer, StringComparer.Ordinal) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (string prefix in _prefixes)
            {
                if (prefix.Length == 0) return true;
                if (path.Equals(prefix, comparison)) return true;
                if (path.Length > prefix.Length && path[prefix.Length] == '/' && path.StartsWith(prefix, comparison)) return true;
            }

            return false;
        }

        /// <summary>
        /// True when the change touches a named path (for a move, either its old or new path).
        /// </summary>
        public bool Matches(WorkspaceChange change)
        {
            return Matches(change.Path) || Matches(change.PreviousPath);
        }
    }
}
