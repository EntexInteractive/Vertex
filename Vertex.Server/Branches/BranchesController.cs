// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;
using Vertex.Server.Branches;

namespace Vertex.Server.Projects
{
    [Route("api/v1/branches")]
    public class BranchesController : Controller
    {
        private readonly BranchesRepository _branches;

        public BranchesController(BranchesRepository branches)
        {
            _branches = branches;
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<VBranch>> CreateBranchAsync([FromQuery] string name, [FromQuery] Guid projectId, [FromQuery] Guid ownerId, [FromQuery] Guid parentId = default, CancellationToken cancellationToken = default)
        {
            VBranch branch = new VBranch(name, projectId, ownerId, parentId);
            if (!await _branches.CreateBranchAsync(branch, cancellationToken)) return BadRequest();
            return StatusCode(StatusCodes.Status201Created, branch);
        }

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<IReadOnlyList<VProject>>> ListBranchesAsync([FromQuery] Guid projectId, CancellationToken cancellationToken)
        {
            IReadOnlyList<VBranch> branches = await _branches.ListBranchesAsync(projectId, cancellationToken);
            return Ok(branches);
        }

        [HttpGet("by-id")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VBranch>> GetBranchByIdAsync([FromQuery] Guid projectId, [FromQuery] string id, CancellationToken cancellationToken)
        {
            VBranch? branch = await _branches.GetBranchByIdAsync(projectId, id, cancellationToken);
            if (branch is null) return NotFound();
            return Ok(branch);
        }

        [HttpGet("by-name")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VBranch>> GetBranchByNameAsync([FromQuery] Guid projectId, [FromQuery] string name, CancellationToken cancellationToken)
        {
            VBranch? branch = await _branches.GetBranchByNameAsync(projectId, name, cancellationToken);
            if (branch is null) return NotFound();
            return Ok(branch);
        }
    }
}