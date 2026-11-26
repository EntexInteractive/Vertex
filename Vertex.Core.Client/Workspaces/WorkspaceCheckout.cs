// Copyright 2026 Entex Interactive

using Vertex.Core.Data;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// What a checkout did.
    /// </summary>
    /// <param name="Workspace">The workspace as it is now, on the new branch when it switched.</param>
    /// <param name="LocalChanges">Files changed here, when the checkout refused to switch because of them.</param>
    /// <param name="Sync">The sync to the new branch, or null when it didn't switch.</param>
    public sealed record CheckoutOutcome(LinkedWorkspace Workspace, bool AlreadyOnBranch, IReadOnlyList<WorkspaceChange> LocalChanges, SyncOutcome? Sync);

    /// <summary>
    /// The workspace switched branches, but its files weren't all updated. A sync finishes the job.
    /// </summary>
    public sealed class CheckoutIncompleteException : Exception
    {
        public CheckoutIncompleteException(string branch, Exception inner)
            : base($"The workspace is on '{branch}' now, but its files aren't all updated yet. Sync to finish.", inner)
        {
            Branch = branch;
        }

        public string Branch { get; }
    }

    /// <summary>
    /// Switches a workspace to another branch of its project: downloads only the files that differ, removes the ones
    /// the branch doesn't have, and follows that branch from then on. Refuses while there are local changes, so
    /// nothing is lost or carried between branches.
    /// </summary>
    /// <remarks>
    /// The config is switched before the files are, with the synced revision reset to 0. If it stops part way, the
    /// workspace is already on the new branch and a sync finishes the job (a sync from revision 0 lists every file and
    /// removes the rest).
    /// </remarks>
    public static class WorkspaceCheckout
    {
        /// <exception cref="CheckoutIncompleteException">The switch happened but the sync after it failed or was canceled.</exception>
        public static async Task<CheckoutOutcome> RunAsync(LinkedWorkspace linked, WorkspaceConnection connection, VBranch branch, IOperationReporter reporter,
            CancellationToken cancellationToken)
        {
            if (branch.Id == linked.BranchId) return new CheckoutOutcome(linked, true, [], null);

            VWorkspace workspace = linked.Workspace;
            using WorkspaceIndex index = workspace.OpenIndex();
            WorkspaceStatus status = await reporter.ScanAsync("Scanning for changes...", workspace, index, new ScanOptions { MaxConcurrency = connection.Parallel },
                cancellationToken);
            if (status.Changes.Count > 0) return new CheckoutOutcome(linked, false, status.Changes, null);

            // The old branch's locks don't apply on the new one; the sync's lock step loads the new branch's.
            WorkspaceLocks.ForgetBranch(workspace, index);
            index.SetMeta(WorkspaceIndex.SyncedRevisionKey, "0");
            VWorkspace switched = workspace.SwitchBranch(branch.Name, branch.Id);
            LinkedWorkspace moved = linked with { Workspace = switched, BranchId = branch.Id };

            try
            {
                SyncOutcome sync = await WorkspaceSync.RunAsync(moved, connection, index, status, 0, reporter, cancellationToken);
                return new CheckoutOutcome(moved, false, [], sync);
            }
            catch (Exception ex)
            {
                throw new CheckoutIncompleteException(branch.Name, ex);
            }
        }
    }
}
