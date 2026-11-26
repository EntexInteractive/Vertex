// Copyright 2026 Entex Interactive

using System.Security.Claims;
using Vertex.Core.Data;
using Vertex.Core.Security;
using Vertex.Server.Access;
using Vertex.Server.History;
using Vertex.Server.Projects;

namespace Vertex.Server.Branches
{
    /// <summary>
    /// An upstream branch named by its project and branch ("UE5", "release"), or why the caller can't use it.
    /// </summary>
    /// <param name="StatusCode">The HTTP status to answer with when <paramref name="Branch"/> is null.</param>
    public sealed record ResolvedUpstream(VProject? Project, VBranch? Branch, int StatusCode = StatusCodes.Status200OK, string? Error = null);

    /// <summary>
    /// What the REST endpoints that link a branch to an upstream share: finding the upstream by name (the caller must be
    /// able to read it) and the first merge that copies its files onto the branch.
    /// </summary>
    public class UpstreamLinks
    {
        private readonly ProjectsRepository _projects;
        private readonly BranchesRepository _branches;
        private readonly AccessService _access;
        private readonly MergesRepository _merges;
        private readonly AutoMerger _autoMerger;
        private readonly RevisionsRepository _revisions;

        public UpstreamLinks(ProjectsRepository projects, BranchesRepository branches, AccessService access, MergesRepository merges, AutoMerger autoMerger,
            RevisionsRepository revisions)
        {
            _revisions = revisions;
            _projects = projects;
            _branches = branches;
            _access = access;
            _merges = merges;
            _autoMerger = autoMerger;
        }

        /// <summary>
        /// Finds <paramref name="projectName"/>/<paramref name="branchName"/> for <paramref name="user"/>, who needs read
        /// access to it.
        /// </summary>
        public async Task<ResolvedUpstream> ResolveAsync(ClaimsPrincipal user, string projectName, string branchName, CancellationToken cancellationToken)
        {
            projectName = projectName.Trim();
            branchName = branchName.Trim();
            if (projectName.Length == 0 || branchName.Length == 0)
                return new ResolvedUpstream(null, null, StatusCodes.Status400BadRequest, "Name the upstream as <project>/<branch>, e.g. UE5/release.");

            VProject? project = await _projects.GetProjectByNameAsync(projectName, cancellationToken);
            if (project is null) return new ResolvedUpstream(null, null, StatusCodes.Status404NotFound, $"Project '{projectName}' doesn't exist.");

            AccessCheck check = await _access.CheckAsync(user, project.Id, AccessLevel.Read, cancellationToken);
            if (!check.Allowed)
            {
                return check.StatusCode == StatusCodes.Status404NotFound
                    ? new ResolvedUpstream(null, null, StatusCodes.Status404NotFound, $"Project '{projectName}' doesn't exist.")
                    : new ResolvedUpstream(null, null, check.StatusCode, $"You can't read project '{project.Name}'. Ask one of its admins for read access ('vx access grant <you> read' in a workspace of it).");
            }

            VBranch? branch = await _branches.GetBranchByNameAsync(project.Id, branchName, cancellationToken);
            return branch is null
                ? new ResolvedUpstream(project, null, StatusCodes.Status404NotFound, $"Project '{project.Name}' has no branch '{branchName}'.")
                : new ResolvedUpstream(project, branch);
        }

        /// <summary>
        /// Takes in the upstream's latest on the branch: on a branch just linked, copies its files ("Imported UE5/release
        /// #500"), metadata only, as one revision.
        /// </summary>
        public async Task<MergeOutcome> ImportAsync(Guid projectId, Guid branchId, VUpstream link, Guid userId, CancellationToken cancellationToken)
        {
            // The number is for the message only; the merge reads the head again itself.
            long head = await _revisions.GetHeadRevisionAsync(link.UpstreamBranchId, cancellationToken);
            string message = head > 0 ? $"Imported {link.Name} #{head}" : $"Imported {link.Name}";
            MergeOutcome outcome = await _merges.MergeAsync(projectId, link.UpstreamBranchId, branchId, userId, null, null, message, MergeResolve.None, cancellationToken,
                sourceProjectId: link.UpstreamProjectId);
            await _autoMerger.PublishAsync(projectId, branchId, outcome);
            return outcome;
        }
    }
}
