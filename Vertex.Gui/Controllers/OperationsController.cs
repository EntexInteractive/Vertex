// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;
using Vertex.Gui.Hosting;
using Vertex.Server;

namespace Vertex.Gui.Controllers
{
    /// <param name="Message">What changed, shown in the project's history.</param>
    /// <param name="Paths">The files to submit, as listed by status. Ignored when <paramref name="All"/> is set.</param>
    /// <param name="All">Submit every change.</param>
    public sealed record SubmitRequest(string Message, IReadOnlyList<string>? Paths, bool All);

    /// <param name="Revision">The revision to sync to, or null for the latest.</param>
    public sealed record SyncRequest(long? Revision);

    /// <param name="Paths">Files or folders to revert, workspace-relative.</param>
    public sealed record RevertRequest(IReadOnlyList<string> Paths);

    public sealed record CheckoutRequest(Guid BranchId);

    /// <summary>
    /// Long workspace operations: submit, sync, revert and checkout. Each starts in the background and returns at
    /// once; progress and the result arrive as <c>operation.progress</c> and <c>operation.done</c> events.
    /// </summary>
    [ApiController]
    [Route("api/operations")]
    public sealed class OperationsController(OperationRunner runner, WorkspaceRegistry registry, ServerSession session) : ControllerBase
    {
        /// <summary>Files named in a summary before "and N more".</summary>
        private const int Listed = 3;

        /// <summary>
        /// The running operation, or 204 when none is.
        /// </summary>
        [HttpGet("current")]
        [ProducesResponseType<OperationDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult Current()
        {
            OperationDto? current = runner.Current;
            return current is null ? NoContent() : Ok(current);
        }

        /// <summary>
        /// Cancels the running operation. What already finished is kept.
        /// </summary>
        [HttpPost("{id:guid}/cancel")]
        public IActionResult Cancel(Guid id)
        {
            return runner.Cancel(id) ? NoContent() : NotFound();
        }

        /// <summary>
        /// Submits changes as one revision, like <c>vx submit</c>.
        /// </summary>
        [HttpPost("submit")]
        public async Task<OperationDto> Submit(SubmitRequest request, CancellationToken cancellationToken)
        {
            string message = request.Message?.Trim() ?? "";
            if (message.Length == 0) throw new HostProblemException("Describe what changed before submitting.");
            if (!request.All && (request.Paths is null || request.Paths.Count == 0)) throw new HostProblemException("Pick the files to submit.");
            await registry.RequiredAsync(cancellationToken);

            PathFilter filter = request.All ? PathFilter.All : PathFilter.FromRelative(request.Paths!);
            string title = request.All || request.Paths!.Count != 1 ? "Submitting" : $"Submitting {Path.GetFileName(request.Paths[0])}";
            return runner.Start("submit", title, async (linked, reporter, ct) =>
            {
                SubmitOutcome outcome = await WorkspaceSubmit.RunAsync(linked, registry.Connection(linked), filter, message, reporter, ct);
                List<string> warnings = [];
                if (outcome.Unreadable.Count > 0)
                    warnings.Add($"Skipped {outcome.Unreadable.Count:N0} file(s) that can't be read, e.g. {outcome.Unreadable[0]}. Close the program using them and submit again.");

                switch (outcome.Result)
                {
                    case SubmitResult.NothingToSubmit:
                        return OperationResultDto.Refused("Nothing to submit: those files have no changes any more.", warnings);
                    case SubmitResult.Blocked:
                        HeldLock first = outcome.Blocked[0];
                        string more = outcome.Blocked.Count > 1 ? $" (and {outcome.Blocked.Count - 1:N0} more)" : "";
                        return OperationResultDto.Refused($"Can't submit {first.Path}{more}: {DescribeLock(first)}.", warnings);
                }

                warnings.AddRange(WorkspacesController.Warnings(outcome.Locks));
                IReadOnlyList<AutoMergeResult> hops = outcome.Response?.AutoMerges ?? [];
                List<string> merged = hops.Where(h => h.Revision > 0).Select(h => $"{h.TargetBranch} as #{h.Revision}").ToList();
                AutoMergeResult? last = hops.Count > 0 ? hops[^1] : null;
                if (last is { Revision: 0 })
                {
                    int conflicts = Math.Max(last.ConflictsTotal, last.Conflicts.Count);
                    warnings.Add(conflicts > 0
                        ? $"Automatic merge from {last.SourceBranch} into {last.TargetBranch} stopped: {conflicts:N0} file(s) changed on both. Settle it with 'vx merge {last.SourceBranch} --into {last.TargetBranch}'."
                        : $"Automatic merge from {last.SourceBranch} into {last.TargetBranch} stopped: {last.Error}");
                }

                string mergedText = merged.Count > 0 ? $" Merged into {string.Join(", ", merged)}." : "";
                return OperationResultDto.Success($"Submitted #{outcome.Revision} ({outcome.Files:N0} file(s)).{mergedText}", outcome.Revision, warnings);
            });
        }

        /// <summary>
        /// Gets the branch's latest revision (or <see cref="SyncRequest.Revision"/>), like <c>vx sync</c>. Local
        /// changes are never overwritten; files changed on both sides come back as conflicts.
        /// </summary>
        [HttpPost("sync")]
        public async Task<OperationDto> Sync(SyncRequest request, CancellationToken cancellationToken)
        {
            await registry.RequiredAsync(cancellationToken);
            long revision = request.Revision is > 0 ? request.Revision.Value : 0;
            return runner.Start("sync", revision == 0 ? "Syncing" : $"Syncing to #{revision}", async (linked, reporter, ct) =>
            {
                SyncOutcome outcome = await WorkspaceSync.RunAsync(linked, registry.Connection(linked), revision, reporter, ct);
                HeadChanges head = outcome.Head;
                string behind = head.HeadRevision < head.LatestRevision ? $" (latest is #{head.LatestRevision})" : "";
                string summary = !outcome.Changed && outcome.Conflicts.Count == 0
                    ? $"Up to date at #{head.HeadRevision}{behind}."
                    : $"Synced to #{head.HeadRevision}{behind}: {outcome.Updated:N0} updated, {outcome.Deleted:N0} deleted.";

                List<string> warnings = WorkspacesController.Warnings(outcome.Locks).ToList();
                if (outcome.Conflicts.Count > 0)
                    warnings.Add($"{outcome.Conflicts.Count:N0} file(s) changed both here and on the server were left as they are. Revert them to take the server's version, then sync again.");

                return OperationResultDto.Success(summary, head.HeadRevision, warnings, outcome.Conflicts);
            });
        }

        /// <summary>
        /// Discards local changes to the named files, like <c>vx revert</c>. New files are left alone.
        /// </summary>
        [HttpPost("revert")]
        public async Task<OperationDto> Revert(RevertRequest request, CancellationToken cancellationToken)
        {
            if (request.Paths is not { Count: > 0 }) throw new HostProblemException("Pick the files to revert.");
            await registry.RequiredAsync(cancellationToken);

            PathFilter filter = PathFilter.FromRelative(request.Paths);
            string title = request.Paths.Count == 1 ? $"Reverting {Path.GetFileName(request.Paths[0])}" : "Reverting";
            return runner.Start("revert", title, async (linked, reporter, ct) =>
            {
                RevertOutcome outcome = await WorkspaceRevert.RunAsync(linked, registry.Connection(linked), filter, reporter, ct);
                if (outcome.NothingToRevert) return OperationResultDto.Refused("Nothing to revert: those files have no changes any more.");

                List<string> warnings = WorkspacesController.Warnings(outcome.Locks).ToList();
                if (outcome.LeftAdded.Count > 0)
                {
                    warnings.Add($"{outcome.LeftAdded.Count:N0} new file(s) were left as they are ({Names(outcome.LeftAdded)}): the server has no earlier version. " +
                                 "Delete them, or add them to .vxignore to stop tracking them.");
                }

                return outcome.Reverted > 0
                    ? OperationResultDto.Success($"Reverted {outcome.Reverted:N0} file(s).", warnings: warnings)
                    : OperationResultDto.Refused(warnings[^1], warnings[..^1]);
            });
        }

        /// <summary>
        /// Switches the workspace to another branch, like <c>vx checkout</c>. Refused while there are local changes.
        /// </summary>
        [HttpPost("checkout")]
        public async Task<OperationDto> Checkout(CheckoutRequest request, CancellationToken cancellationToken)
        {
            LinkedWorkspace current = await registry.RequiredAsync(cancellationToken);
            using BranchInterface branches = session.Connect((s, token) => new BranchInterface(s, token));
            ServerResult<VBranch> result = await branches.GetBranchByIdAsync(current.ProjectId, request.BranchId, cancellationToken);
            if (!result.IsSuccessStatusCode || result.Data is null)
            {
                if (result.Exception is not null && result.StatusCode == 0) throw result.Exception;
                throw new HostProblemException("That branch doesn't exist any more. Pick another one.", StatusCodes.Status404NotFound);
            }

            VBranch branch = result.Data;
            return runner.Start("checkout", $"Switching to {branch.Name}", async (linked, reporter, ct) =>
            {
                CheckoutOutcome outcome = await WorkspaceCheckout.RunAsync(linked, registry.Connection(linked), branch, reporter, ct);
                if (outcome.AlreadyOnBranch) return OperationResultDto.Success($"Already on {branch.Name}.");
                if (outcome.LocalChanges.Count > 0)
                {
                    return OperationResultDto.Refused($"{outcome.LocalChanges.Count:N0} file(s) changed here ({Names(outcome.LocalChanges.Select(c => c.Path).ToList())}). " +
                                                      "Submit or revert them before switching branches.");
                }

                SyncOutcome sync = outcome.Sync!;
                return OperationResultDto.Success($"Switched to {branch.Name} at #{sync.Head.HeadRevision}: {sync.Updated:N0} updated, {sync.Deleted:N0} deleted.",
                    sync.Head.HeadRevision, WorkspacesController.Warnings(sync.Locks), sync.Conflicts);
            });
        }

        private static string DescribeLock(HeldLock held)
        {
            return held.SameUser
                ? $"you hold its lock in another workspace on {held.Host}. Submit it from there, or release that lock on the Locks page if that workspace is gone"
                : $"locked by {held.Username} on {held.Host}. Wait for them to submit it, or revert your change";
        }

        private static string Names(IReadOnlyList<string> paths)
        {
            string names = string.Join(", ", paths.Take(Listed));
            return paths.Count > Listed ? $"{names} and {paths.Count - Listed:N0} more" : names;
        }
    }
}
