// Copyright 2026 Entex Interactive

using Vertex.Cli.Net;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Net.Interfaces;

namespace Vertex.Cli.Files
{
    /// <summary>
    /// Runs a workspace's lock step for a command and prints its results the same way everywhere.
    /// </summary>
    public static class LockOutput
    {
        /// <summary>
        /// Opens the lock step for a linked workspace.
        /// </summary>
        public static WorkspaceLocks Open(LinkedWorkspace linked, WorkspaceIndex index, LockInterface client, NetworkSettings settings)
        {
            string username = settings.Credential?.Username ?? linked.Workspace.Config.User ?? string.Empty;
            return new WorkspaceLocks(linked.Workspace, index, client, linked.ProjectId, linked.BranchId, linked.WorkspaceId, username);
        }

        /// <summary>
        /// Prints one yellow line when the server couldn't be asked about locks. Local results are still valid.
        /// </summary>
        public static void PrintError(LockReport report)
        {
            if (report.Error is null) return;
            string reason = ServerErrors.Describe(report.Error) ?? (report.Error is OperationCanceledException ? "the server didn't answer in time." : report.Error.Message);
            AnsiConsole.MarkupLine($"[yellow]Couldn't update file locks: {Markup.Escape(reason)}[/]");
        }

        /// <summary>
        /// Who holds a changed file and what to do about it, e.g. "locked by alice on DESKTOP-7; wait for them, or
        /// run 'vx revert Maps/City.umap' to drop your change".
        /// </summary>
        public static string Describe(VWorkspace workspace, HeldLock held)
        {
            string path = SyncCommand.CommandPath(workspace, held.Path);
            return held.SameUser
                ? $"locked by you in another workspace on {held.Host}; submit it from there, or run 'vx unlock {path}' if that workspace is gone"
                : $"locked by {held.Username} on {held.Host}; wait for them to submit it, or run 'vx revert {path}' to drop your change";
        }
    }
}
