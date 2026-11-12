// Copyright 2026 Entex Interactive

using Vertex.Core.Client.IO;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// A local folder linked to a Vertex project, marked by a <c>.vx</c> directory at its root (like <c>.git</c>).
    /// The workspace holds no history: the server does. Locally it only stores which project and branch it
    /// follows (<c>.vx/config</c>) and an index of the files it has seen (<c>.vx/index.db</c>).
    /// </summary>
    public sealed class Workspace
    {
        public const string MetadataDirectoryName = ".vx";
        private const string ConfigFileName = "config";
        private const string IndexFileName = "index.db";

        private readonly string _rootPrefix;

        /// <summary>Full path of the workspace root.</summary>
        public string Root { get; }

        public string MetadataDirectory => Path.Combine(Root, MetadataDirectoryName);

        public WorkspaceConfig Config { get; }

        private Workspace(string root, WorkspaceConfig config)
        {
            Root = root;
            Config = config;
            _rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Finds the workspace containing <paramref name="path"/> by walking up from it, the way git finds
        /// <c>.git</c>.
        /// </summary>
        /// <returns>The workspace, or null when <paramref name="path"/> is not inside one.</returns>
        public static Workspace? Find(string path)
        {
            string? current = Path.GetFullPath(path);
            if (File.Exists(current)) current = Path.GetDirectoryName(current);

            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current, MetadataDirectoryName))) return Open(current);
                current = Path.GetDirectoryName(current);
            }

            return null;
        }

        /// <summary>
        /// Like <see cref="Find"/>, but throws a message suitable for the user when there is no workspace.
        /// </summary>
        public static Workspace FindRequired(string path)
        {
            return Find(path) ?? throw new InvalidOperationException($"'{Path.GetFullPath(path)}' is not inside a Vertex workspace. Run 'vx init' to create one.");
        }

        /// <summary>
        /// Creates a new workspace at <paramref name="root"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException"><paramref name="root"/> is already inside a workspace.</exception>
        public static Workspace Initialize(string root, WorkspaceConfig config)
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            Workspace? existing = Find(root);
            if (existing is not null) throw new InvalidOperationException($"'{existing.Root}' is already a Vertex workspace.");

            DirectoryInfo metadata = Directory.CreateDirectory(Path.Combine(root, MetadataDirectoryName));
            if (OperatingSystem.IsWindows()) metadata.Attributes |= FileAttributes.Hidden;

            config.Save(Path.Combine(metadata.FullName, ConfigFileName));
            using (WorkspaceIndex.Open(Path.Combine(metadata.FullName, IndexFileName)))
            {
                // Opening creates the schema.
            }

            return Open(root);
        }

        private static Workspace Open(string root)
        {
            root = Path.TrimEndingDirectorySeparator(root);
            WorkspaceConfig config = WorkspaceConfig.Load(Path.Combine(root, MetadataDirectoryName, ConfigFileName)) ?? new WorkspaceConfig();
            return new Workspace(root, config);
        }

        /// <summary>
        /// Opens the workspace's file index. Dispose it when done.
        /// </summary>
        public WorkspaceIndex OpenIndex()
        {
            return WorkspaceIndex.Open(Path.Combine(MetadataDirectory, IndexFileName));
        }

        /// <summary>
        /// Converts a path relative to the root, with <c>/</c> separators, to a full path.
        /// </summary>
        public string ToFullPath(string relativePath)
        {
            return _rootPrefix + (Path.DirectorySeparatorChar == '/' ? relativePath : relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// Converts a full path inside the workspace to a relative path with <c>/</c> separators.
        /// </summary>
        public string ToRelativePath(string fullPath)
        {
            return FileSystemUtility.ToRelativePath(_rootPrefix, Path.GetFullPath(fullPath));
        }
    }
}