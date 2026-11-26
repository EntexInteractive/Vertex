// Copyright 2026 Entex Interactive

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// A workspace together with the project, branch and workspace id it follows.
    /// </summary>
    public sealed record LinkedWorkspace(VWorkspace Workspace, Guid ProjectId, Guid BranchId, Guid WorkspaceId)
    {
        /// <summary>
        /// Finds the workspace containing the current folder.
        /// </summary>
        /// <exception cref="InvalidOperationException">Not in a workspace, or the workspace isn't linked to a project.</exception>
        public static LinkedWorkspace FromCurrentDirectory()
        {
            return FromPath(Directory.GetCurrentDirectory());
        }

        /// <summary>
        /// Finds the workspace containing <paramref name="path"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Not in a workspace, or the workspace isn't linked to a project.</exception>
        public static LinkedWorkspace FromPath(string path)
        {
            VWorkspace workspace = VWorkspace.FindRequired(path);
            return TryFrom(workspace)
                   ?? throw new InvalidOperationException($"'{workspace.Root}' isn't linked to a project. Run 'vx clone <project>' in it.");
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
}
