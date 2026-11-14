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
        private readonly BranchesRepository _branches;
        private readonly AccessService _access;

        public BranchesController(BranchesRepository branches, AccessService access)
        {
            _branches = branches;
            _access = access;
        }

        /// <summary>
        /// Creating and deleting branches needs project admin; reading them needs read.
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
        public async Task<ActionResult<VBranch>> CreateBranchAsync([FromQuery] string name, [FromQuery] Guid projectId, [FromQuery] Guid? parentId = null, CancellationToken cancellationToken = default)
        {
            if (await DenyAsync(projectId, AccessLevel.Admin, cancellationToken) is ActionResult denied) return denied;

            VBranch branch = new VBranch(name, projectId, User.GetUserId(), parentId);
            if (!await _branches.AddBranchAsync(branch, cancellationToken))
            {
                // Only look up why on the failure path, so a successful create stays one query.
                if (await _branches.GetBranchByNameAsync(projectId, name, cancellationToken) is not null)
                    return Conflict($"Branch '{name}' already exists.");

                return BadRequest();
            }

            return StatusCode(StatusCodes.Status201Created, branch);
        }

        [HttpDelete]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<VBranch>> DeleteBranchAsync([FromQuery] Guid projectId, [FromQuery] string name, CancellationToken cancellationToken)
        {
            if (await DenyAsync(projectId, AccessLevel.Admin, cancellationToken) is ActionResult denied) return denied;

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