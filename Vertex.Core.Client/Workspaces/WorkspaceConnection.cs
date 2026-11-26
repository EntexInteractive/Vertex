// Copyright 2026 Entex Interactive

using Vertex.Core.Net.Interfaces;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// How a workspace operation reaches the server: the address, the login token and who that login is.
    /// </summary>
    /// <param name="Username">The logged-in user, used to tell this user's locks from others'.</param>
    /// <param name="MaxConcurrency">How many files are hashed, uploaded or downloaded at once.</param>
    public sealed record WorkspaceConnection(string ServerAddress, string? Token, string? Username, int MaxConcurrency)
    {
        /// <summary>At least one.</summary>
        public int Parallel => Math.Max(1, MaxConcurrency);

        /// <summary>
        /// Opens the lock step for a linked workspace.
        /// </summary>
        public WorkspaceLocks OpenLocks(LinkedWorkspace linked, WorkspaceIndex index, LockInterface client)
        {
            string username = Username ?? linked.Workspace.Config.User ?? string.Empty;
            return new WorkspaceLocks(linked.Workspace, index, client, linked.ProjectId, linked.BranchId, linked.WorkspaceId, username);
        }
    }
}
