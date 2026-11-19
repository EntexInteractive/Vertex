// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Server.Branches;
using Vertex.Server.Database;
using Vertex.Server.Events;
using Vertex.Server.Telemetry;

namespace Vertex.Server.History
{
    /// <summary>
    /// Runs a branch's merge chain (<see cref="VBranch.MergeTo"/>) after a revision lands on it, the way release fixes
    /// flow forward: release-1.0 → release-2.0 → main. Also does the bookkeeping every merge needs after it commits.
    /// </summary>
    /// <remarks>
    /// Each hop is an ordinary merge (<see cref="MergesRepository.MergeAsync"/>) with no conflict resolution, so a
    /// hop never overwrites anything: files changed on both stop the chain there. The chain continues from a target
    /// only when the hop wrote a revision. It runs on the server right after the revision that starts it, so every
    /// client (the CLI, the GUI, vx-mirror) gets it. A hop that doesn't happen (the server stopped, a conflict) is
    /// caught up by the next one, because a merge takes everything since the last merge between the two.
    /// </remarks>
    public class AutoMerger
    {
        /// <summary>Hops followed at most; a longer chain is cut off (the targets are validated to never loop).</summary>
        private const int MaximumHops = 16;

        /// <summary>Conflicts listed per hop; the result gives the total.</summary>
        private const int ListedConflicts = 1_000;

        private readonly ILogger<AutoMerger> _logger;
        private readonly MergesRepository _merges;
        private readonly BranchesRepository _branches;
        private readonly RevisionsRepository _revisions;
        private readonly CacheContext _cache;
        private readonly INotificationService _notifications;

        public AutoMerger(ILogger<AutoMerger> logger, MergesRepository merges, BranchesRepository branches, RevisionsRepository revisions, CacheContext cache, INotificationService notifications)
        {
            _logger = logger;
            _merges = merges;
            _branches = branches;
            _revisions = revisions;
            _cache = cache;
            _notifications = notifications;
        }

        /// <summary>
        /// Follows the merge chain from <paramref name="branchId"/>, which just got <paramref name="revision"/>.
        /// </summary>
        /// <returns>One result per hop tried; empty when the branch has no merge target.</returns>
        public async Task<IReadOnlyList<AutoMergeResult>> RunAsync(Guid projectId, Guid branchId, long revision, string message, Guid userId, Guid? workspaceId, string? host)
        {
            // Not the caller's token: once a revision is in, its chain should finish even if the client hangs up.
            CancellationToken ct = CancellationToken.None;
            List<AutoMergeResult> results = new();
            HashSet<Guid> visited = [branchId];
            Guid current = branchId;
            string original = FirstLine(message);
            try
            {
                for (int hop = 0; hop < MaximumHops; hop++)
                {
                    VBranch? source = await _branches.GetBranchByIdAsync(projectId, current, ct);
                    if (source?.MergeTo is not Guid targetId || !visited.Add(targetId)) break;
                    VBranch? target = await _branches.GetBranchByIdAsync(projectId, targetId, ct);
                    if (target is null) break;

                    AutoMergeResult result = new() { SourceBranchId = source.Id.ToString(), SourceBranch = source.Name, TargetBranchId = target.Id.ToString(), TargetBranch = target.Name };
                    results.Add(result);

                    string hopMessage = $"Merged {source.Name} #{revision} into {target.Name}";
                    MergeOutcome outcome;
                    try
                    {
                        outcome = await _merges.MergeAsync(projectId, source.Id, target.Id, userId, workspaceId, host, hopMessage, MergeResolve.None, ct, automatic: true);
                    }
                    catch (TimeoutException ex)
                    {
                        result.Error = ex.Message;
                        break;
                    }

                    await PublishAsync(projectId, target.Id, outcome);
                    switch (outcome.Status)
                    {
                        case MergeStatus.Conflicts:
                            result.ConflictsTotal = outcome.Conflicts!.Count;
                            result.Conflicts.AddRange(outcome.Conflicts.Take(ListedConflicts).Select(ToMessage));
                            break;
                        case MergeStatus.Locked:
                            result.Error = DescribeLocked(target.Name, outcome.Locks!);
                            break;
                        case MergeStatus.Unrelated:
                            result.Error = Unrelated(source.Name, target.Name);
                            break;
                    }

                    if (outcome.Revision is not long merged) break;
                    result.Revision = merged;
                    result.Files = outcome.Files;
                    _logger.LogInformation("Auto-merged {Source} #{Revision} into {Target} as #{Merged} in project {ProjectId}: {Files} file(s)", source.Name, revision, target.Name, merged, projectId, outcome.Files);

                    current = target.Id;
                    revision = merged;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The revision that started the chain is in; report the hop instead of failing the whole call.
                _logger.LogError(ex, "Auto-merge from branch {BranchId} in project {ProjectId} failed", current, projectId);
                if (results.Count > 0 && results[^1].Revision == 0 && results[^1].Error.Length == 0) results[^1].Error = "The server couldn't run the merge. Try 'vx merge' by hand.";
            }

            return results;
        }

        /// <summary>
        /// After a merge commits: wakes everyone following the project, caches the target's new head and records the
        /// metrics. Every merge goes through here, by hand or automatic.
        /// </summary>
        public async Task PublishAsync(Guid projectId, Guid targetId, MergeOutcome outcome)
        {
            VertexMetrics.Merges.Add(1, VertexMetrics.Tag("result", outcome.Status switch
            {
                MergeStatus.Merged => "merged",
                MergeStatus.UpToDate => "up_to_date",
                MergeStatus.Conflicts => "conflicts",
                MergeStatus.Locked => "locked",
                _ => "unrelated"
            }));

            await ProjectCalls.AfterCommitAsync(_cache, _notifications, projectId, outcome.EventSeq);
            if (outcome.Status == MergeStatus.Conflicts) VertexMetrics.EventsRecorded.Add(1, VertexMetrics.Tag("type", EventTypes.MergeRefused));
            if (outcome.Status != MergeStatus.Merged) return;

            VertexMetrics.EventsRecorded.Add(1, VertexMetrics.Tag("type", EventTypes.MergeCompleted));
            if (outcome.Revision is long revision)
            {
                await _revisions.CacheHeadRevisionAsync(targetId, revision);
                VertexMetrics.Revisions.Add(1);
                VertexMetrics.RevisionFiles.Record(outcome.Files);
                VertexMetrics.EventsRecorded.Add(1, VertexMetrics.Tag("type", EventTypes.RevisionCreated));
            }
        }

        public static MergeConflict ToMessage(MergeConflictRow conflict)
        {
            return new MergeConflict { Path = conflict.Path, SourceFileId = conflict.SourceFileId ?? string.Empty, TargetFileId = conflict.TargetFileId ?? string.Empty };
        }

        /// <summary>
        /// "'a.txt' is locked on main by alice on DESKTOP-7. Wait for it to be submitted, then merge again."
        /// </summary>
        public static string DescribeLocked(string target, IReadOnlyList<LockRow> locks)
        {
            LockRow first = locks[0];
            string more = locks.Count > 1 ? $" (and {locks.Count - 1:N0} more locked file(s))" : "";
            return $"'{first.Path}' is locked on {target} by {first.Username} on {first.Host}{more}. Wait for it to be submitted, then merge again.";
        }

        public static string Unrelated(string source, string target)
        {
            return $"'{source}' and '{target}' don't share any files' history (one of them was created with --blank), so they can't be merged.";
        }

        private static string FirstLine(string message)
        {
            int end = message.IndexOfAny(['\r', '\n']);
            return end < 0 ? message : message[..end];
        }
    }
}
