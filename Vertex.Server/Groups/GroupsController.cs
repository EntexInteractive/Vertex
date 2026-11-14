// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;
using Vertex.Server.Access;
using Vertex.Server.Auth;
using Vertex.Server.Users;

namespace Vertex.Server.Groups
{
    /// <summary>
    /// Group management. Anyone logged in can list groups and their members (to know whom to ask for access);
    /// changes need a server admin.
    /// </summary>
    [Route("api/v1/groups")]
    public class GroupsController : Controller
    {
        private const string AdminOnly = "Only server admins can manage groups.";

        private readonly GroupsRepository _groups;
        private readonly AccessService _access;

        public GroupsController(GroupsRepository groups, AccessService access)
        {
            _groups = groups;
            _access = access;
        }

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IReadOnlyList<VGroup>>> ListGroupsAsync(CancellationToken cancellationToken)
        {
            return Ok(await _groups.ListGroupsAsync(cancellationToken));
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<VGroup>> CreateGroupAsync([FromQuery] string name, CancellationToken cancellationToken)
        {
            if (!User.IsServerAdmin()) return StatusCode(StatusCodes.Status403Forbidden, AdminOnly);

            string? nameError = PrincipalNames.Validate(name);
            if (nameError is not null) return BadRequest(nameError);

            VGroup? group = await _groups.CreateGroupAsync(name.Trim(), cancellationToken);
            if (group is null) return Conflict($"A user or group named '{name.Trim()}' already exists.");
            return StatusCode(StatusCodes.Status201Created, group);
        }

        /// <summary>
        /// Deletes a group and removes its project grants.
        /// </summary>
        [HttpDelete]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<bool>> DeleteGroupAsync([FromQuery] string name, CancellationToken cancellationToken)
        {
            if (!User.IsServerAdmin()) return StatusCode(StatusCodes.Status403Forbidden, AdminOnly);
            if (!await _groups.DeleteGroupAsync(name, cancellationToken)) return NotFound($"Group '{name}' does not exist.");

            await _access.InvalidateAllAsync();
            return Ok(true);
        }

        [HttpGet("members")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<IReadOnlyList<VUser>>> ListMembersAsync([FromQuery] string name, CancellationToken cancellationToken)
        {
            VGroup? group = await _groups.GetGroupByNameAsync(name, cancellationToken);
            if (group is null) return NotFound($"Group '{name}' does not exist. Run 'vx group list' to see all groups.");
            return Ok(await _groups.ListMembersAsync(group.Id, cancellationToken));
        }

        /// <summary>
        /// Adds users to a group. Users who are already members are skipped, so re-running is safe.
        /// </summary>
        /// <return>200 with how many users were added, or 404 naming the group or users that don't exist.</return>
        [HttpPost("members")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<int>> AddMembersAsync([FromQuery] string name, [FromQuery] string[] users, CancellationToken cancellationToken)
        {
            if (!User.IsServerAdmin()) return StatusCode(StatusCodes.Status403Forbidden, AdminOnly);
            if (users.Length == 0) return BadRequest("Name at least one user to add.");

            VGroup? group = await _groups.GetGroupByNameAsync(name, cancellationToken);
            if (group is null) return NotFound($"Group '{name}' does not exist. Create it with 'vx group create {name}'.");

            IReadOnlyList<string> unknown = await _groups.FindUnknownUsersAsync(users, cancellationToken);
            if (unknown.Count > 0) return NotFound($"No user named {string.Join(", ", unknown.Select(u => $"'{u}'"))}. Run 'vx user list' to see all users.");

            int added = await _groups.AddMembersAsync(group.Id, users, cancellationToken);
            if (added > 0) await _access.InvalidateAllAsync();
            return Ok(added);
        }

        /// <summary>
        /// Removes users from a group. Users who aren't members are skipped.
        /// </summary>
        [HttpDelete("members")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<int>> RemoveMembersAsync([FromQuery] string name, [FromQuery] string[] users, CancellationToken cancellationToken)
        {
            if (!User.IsServerAdmin()) return StatusCode(StatusCodes.Status403Forbidden, AdminOnly);
            if (users.Length == 0) return BadRequest("Name at least one user to remove.");

            VGroup? group = await _groups.GetGroupByNameAsync(name, cancellationToken);
            if (group is null) return NotFound($"Group '{name}' does not exist.");

            int removed = await _groups.RemoveMembersAsync(group.Id, users, cancellationToken);
            if (removed > 0) await _access.InvalidateAllAsync();
            return Ok(removed);
        }
    }
}
