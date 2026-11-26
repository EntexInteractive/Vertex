// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;
using Vertex.Core.Security;
using Vertex.Server.Access;
using Vertex.Server.Auth;
using Vertex.Server.Branches;

namespace Vertex.Server.Projects
{
    [Route("api/v1/branches")]
    public class BranchesController : Controller
    {
        /// <summary>The <c>paths</c> value that puts a link back to the upstream's own top-level folders and files.</summary>
        private const string AutoPaths = "auto";

        private readonly BranchesRepository _branches;
        private readonly AccessService _access;
        private readonly UpstreamsRepository _upstreams;
        private readonly UpstreamLinks _links;

        public BranchesController(BranchesRepository branches, AccessService access, UpstreamsRepository upstreams, UpstreamLinks links)
        {
            _branches = branches;
            _access = access;
            _upstreams = upstreams;
            _links = links;
        }

        /// <summary>
        /// Creating and deleting branches needs manage access; reading them needs read.
        /// </summary>
        /// <returns>Null when allowed, otherwise the response to return.</returns>
        private async Task<ActionResult?> DenyAsync(Guid projectId, AccessLevel required, CancellationToken cancellationToken)
        {
            AccessCheck check = await _access.CheckAsync(User, projectId, required, cancellationToken);
            return check.Allowed ? null : StatusCode(check.StatusCode, check.Message);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<VBranch>> CreateBranchAsync([FromQuery] string name, [FromQuery] Guid projectId, [FromQuery] Guid? parentId = null,
            [FromQuery] Guid? mergeToId = null, CancellationToken cancellationToken = default)
        {
            if (await DenyAsync(projectId, AccessLevel.Manage, cancellationToken) is ActionResult denied) return denied;

            // With a parent, the branch starts with the parent's files; without one it starts empty. With a merge
            // target, every revision on it is merged on into that branch.
            VBranch branch = new VBranch(name, projectId, parentId, mergeToId);
            bool added;
            try
            {
                added = await _branches.AddBranchAsync(branch, User.GetUserId(), cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }

            if (!added)
            {
                // Only look up why on the failure path, so a successful create stays one query.
                if (await _branches.GetBranchByNameAsync(projectId, name, cancellationToken) is not null)
                    return Conflict($"Branch '{name}' already exists.");

                return BadRequest();
            }

            return StatusCode(StatusCodes.Status201Created, branch);
        }

        /// <summary>
        /// Sets the branch a branch's revisions are merged into automatically; without <paramref name="mergeToId"/>,
        /// clears it. Responds with the branch and, in <c>previousMergeTo</c>, the target it had before.
        /// </summary>
        [HttpPut("merge-to")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<MergeToChange>> SetMergeToAsync([FromQuery] Guid projectId, [FromQuery] Guid branchId, [FromQuery] Guid? mergeToId = null,
            CancellationToken cancellationToken = default)
        {
            if (await DenyAsync(projectId, AccessLevel.Manage, cancellationToken) is ActionResult denied) return denied;

            try
            {
                MergeToChange? change = await _branches.SetMergeToAsync(projectId, branchId, mergeToId, cancellationToken);
                if (change is null) return NotFound();
                return Ok(change);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// A branch's upstreams (at most one for now) and how far apart they are: the upstream revision the branch took
        /// in last and how many came after it; with <paramref name="differences"/>, also how many of the files the
        /// upstream owns differ between the two (reads both file lists, so only <c>vx upstream</c> asks).
        /// </summary>
        [HttpGet("upstream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IReadOnlyList<VUpstream>>> GetUpstreamsAsync([FromQuery] Guid projectId, [FromQuery] Guid branchId, [FromQuery] bool differences = false,
            CancellationToken cancellationToken = default)
        {
            if (await DenyAsync(projectId, AccessLevel.Read, cancellationToken) is ActionResult denied) return denied;
            return Ok(await _upstreams.ListAsync(projectId, branchId, true, differences, cancellationToken));
        }

        /// <summary>
        /// Makes <paramref name="upstreamProject"/>/<paramref name="upstreamBranch"/> the branch's upstream (replacing any
        /// other) or changes its settings. Linking stores the link only: the next <c>vx merge upstream</c> takes the
        /// upstream's files in. Needs manage access to the branch's project and read access to the upstream's.
        /// </summary>
        /// <param name="projectId">The branch's project.</param>
        /// <param name="branchId">The branch to link (the downstream).</param>
        /// <param name="upstreamProject">The upstream's project, by name.</param>
        /// <param name="upstreamBranch">The upstream branch, by name.</param>
        /// <param name="paths">Comma-separated folders and files the upstream owns, or <c>auto</c> for its top-level
        /// ones. Omitted, the link keeps what it has.</param>
        /// <param name="readOnly">Refuse submits to the upstream's paths on the branch. Omitted, the link keeps its setting.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        [HttpPut("upstream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VUpstream>> SetUpstreamAsync([FromQuery] Guid projectId, [FromQuery] Guid branchId, [FromQuery] string upstreamProject,
            [FromQuery] string upstreamBranch, [FromQuery] string? paths = null, [FromQuery] bool? readOnly = null, CancellationToken cancellationToken = default)
        {
            if (await DenyAsync(projectId, AccessLevel.Manage, cancellationToken) is ActionResult denied) return denied;

            ResolvedUpstream upstream = await _links.ResolveAsync(User, upstreamProject ?? "", upstreamBranch ?? "", cancellationToken);
            if (upstream.Branch is null) return StatusCode(upstream.StatusCode, upstream.Error);

            try
            {
                string[]? entries = paths is null ? null : paths.Trim().Equals(AutoPaths, StringComparison.OrdinalIgnoreCase) ? [] : UpstreamsRepository.Normalize(paths.Split(','));
                VUpstream? link = await _upstreams.SetAsync(projectId, branchId, upstream.Branch.Id, entries, readOnly, cancellationToken);
                if (link is null) return NotFound("The branch no longer exists. Run 'vx branch list' to see the project's branches.");
                return Ok(link);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Unlinks a branch from its upstream. Its files and history stay; it just no longer takes or sends changes.
        /// </summary>
        /// <returns>The links removed (empty when it had none).</returns>
        [HttpDelete("upstream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IReadOnlyList<VUpstream>>> ClearUpstreamAsync([FromQuery] Guid projectId, [FromQuery] Guid branchId, CancellationToken cancellationToken)
        {
            if (await DenyAsync(projectId, AccessLevel.Manage, cancellationToken) is ActionResult denied) return denied;
            return Ok(await _upstreams.ClearAsync(projectId, branchId, cancellationToken));
        }

        [HttpDelete]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<VBranch>> DeleteBranchAsync([FromQuery] Guid projectId, [FromQuery] string name, CancellationToken cancellationToken)
        {
            if (await DenyAsync(projectId, AccessLevel.Manage, cancellationToken) is ActionResult denied) return denied;

            try
            {
                VBranch? branch = await _branches.DeleteBranchAsync(projectId, name, cancellationToken);
                if (branch is null) return NotFound();
                return Ok(branch);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<IReadOnlyList<VBranch>>> ListBranchesAsync([FromQuery] Guid projectId, CancellationToken cancellationToken)
        {
            if (await DenyAsync(projectId, AccessLevel.Read, cancellationToken) is ActionResult denied) return denied;

            IReadOnlyList<VBranch> branches = await _branches.ListBranchesAsync(projectId, cancellationToken);
            return Ok(branches);
        }

        [HttpGet("by-id")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VBranch>> GetBranchByIdAsync([FromQuery] Guid projectId, [FromQuery] Guid branchId, CancellationToken cancellationToken)
        {
            if (await DenyAsync(projectId, AccessLevel.Read, cancellationToken) is ActionResult denied) return denied;

            VBranch? branch = await _branches.GetBranchByIdAsync(projectId, branchId, cancellationToken);
            if (branch is null) return NotFound();
            return Ok(branch);
        }

        [HttpGet("by-name")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VBranch>> GetBranchByNameAsync([FromQuery] Guid projectId, [FromQuery] string name, CancellationToken cancellationToken)
        {
            if (await DenyAsync(projectId, AccessLevel.Read, cancellationToken) is ActionResult denied) return denied;

            VBranch? branch = await _branches.GetBranchByNameAsync(projectId, name, cancellationToken);
            if (branch is null) return NotFound();
            return Ok(branch);
        }
    }
}