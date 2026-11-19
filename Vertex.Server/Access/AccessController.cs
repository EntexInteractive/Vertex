// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;
using Vertex.Core.Net.Requests;
using Vertex.Core.Security;

namespace Vertex.Server.Access
{
    /// <summary>
    /// A project's grants. Anyone who can read the project can see its grants, so they know whom to ask; changing
    /// them needs manage access.
    /// </summary>
    [Route("api/v1/access")]
    public class AccessController : Controller
    {
        private readonly AccessService _access;

        public AccessController(AccessService access)
        {
            _access = access;
        }

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<IReadOnlyList<VAccessGrant>>> ListGrantsAsync([FromQuery] Guid projectId, CancellationToken cancellationToken)
        {
            AccessCheck check = await _access.CheckAsync(User, projectId, AccessLevel.Read, cancellationToken);
            if (!check.Allowed) return StatusCode(check.StatusCode, check.Message);
            return Ok(await _access.ListGrantsAsync(projectId, cancellationToken));
        }

        /// <summary>
        /// Gives a user or group a level in the project, replacing any level it had. Safe to re-run.
        /// </summary>
        [HttpPut]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VAccessGrant>> GrantAsync([FromQuery] Guid projectId, [FromBody] GrantAccessRequest request, CancellationToken cancellationToken)
        {
            if (request.Level == AccessLevel.Admin) return BadRequest("Server admin isn't a project level. Use 'vx user admin <name>' to make someone a server admin.");
            if (request.Level is < AccessLevel.Read or > AccessLevel.Manage) return BadRequest("The level must be read, write or manage.");

            AccessCheck check = await _access.CheckAsync(User, projectId, AccessLevel.Manage, cancellationToken);
            if (!check.Allowed) return StatusCode(check.StatusCode, check.Message);

            VAccessGrant? grant = await _access.GrantAsync(projectId, request.Name.Trim(), request.Level, cancellationToken);
            if (grant is null) return NotFound($"No user or group is named '{request.Name}'. Run 'vx user list' or 'vx group list' to see them.");
            return Ok(grant);
        }

        /// <summary>
        /// Removes a user's or group's grant. Its members keep any access from their own grant or other groups.
        /// </summary>
        [HttpDelete]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<bool>> RevokeAsync([FromQuery] Guid projectId, [FromQuery] string name, CancellationToken cancellationToken)
        {
            AccessCheck check = await _access.CheckAsync(User, projectId, AccessLevel.Manage, cancellationToken);
            if (!check.Allowed) return StatusCode(check.StatusCode, check.Message);
            return Ok(await _access.RevokeAsync(projectId, name.Trim(), cancellationToken));
        }
    }
}
