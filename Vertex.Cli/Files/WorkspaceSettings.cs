// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;
using Vertex.Core.Client.Workspaces;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Settings for commands that work on the workspace containing the current folder (submit, sync, revert). The
    /// project and branch always come from the workspace.
    /// </summary>
    public class WorkspaceSettings : NetworkSettings
    {
        [CommandOption("--parallel")]
        [Description("The number of parallel operations to perform.")]
        public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
    }

    /// <summary>
    /// The workspace a command runs in, with the project and branch it follows.
    /// </summary>
    public sealed record LinkedWorkspace(VWorkspace Workspace, Guid ProjectId, Guid BranchId, Guid WorkspaceId)
    {
        /// <summary>
        /// Finds the workspace containing the current folder.
        /// </summary>
        /// <exception cref="InvalidOperationException">Not in a workspace, or the workspace isn't linked to a project.</exception>
        public static LinkedWorkspace FromCurrentDirectory()
        {
            VWorkspace workspace = VWorkspace.FindRequired(Directory.GetCurrentDirectory());
            WorkspaceConfig config = workspace.Config;
            if (config.ProjectId is not Guid projectId || config.BranchId is not Guid branchId)
                throw new InvalidOperationException($"'{workspace.Root}' isn't linked to a project. Run 'vx fetch <project>' in it.");

            return new LinkedWorkspace(workspace, projectId, branchId, config.WorkspaceId!.Value);
        }

        /// <summary>
        /// The workspace with its project and branch, or null when it isn't linked to a project.
        /// </summary>
        public static LinkedWorkspace? TryFrom(VWorkspace workspace)
        {
            WorkspaceConfig config = workspace.Config;
            return config is { ProjectId: Guid projectId, BranchId: Guid branchId, WorkspaceId: Guid workspaceId }
                ? new LinkedWorkspace(workspace, projectId, branchId, workspaceId)
                : null;
        }
    }

    /// <summary>
    /// The files and folders named on the command line, as workspace-relative paths. No paths means the current folder.
    /// </summary>
    public sealed class PathFilter
    {
        private readonly List<string> _prefixes;

        private PathFilter(List<string> prefixes)
        {
            _prefixes = prefixes;
        }

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
        /// True when <paramref name="path"/> is one of the named files or inside one of the named folders.
        /// </summary>
        public bool Matches(string? path)
        {
            if (path is null) return false;
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
