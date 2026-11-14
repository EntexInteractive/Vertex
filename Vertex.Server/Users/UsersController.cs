// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;
using Vertex.Core.Net.Requests;
using Vertex.Server.Access;
using Vertex.Server.Auth;

namespace Vertex.Server.Users
{
    /// <summary>
    /// User management. Everything except changing your own password needs a server admin.
    /// </summary>
    [Route("api/v1/users")]
    public class UsersController : Controller
    {
        private const string AdminOnly = "Only server admins can manage users.";

        private readonly UsersRepository _users;
        private readonly TokensRepository _tokens;
        private readonly AccessService _access;

        public UsersController(UsersRepository users, TokensRepository tokens, AccessService access)
        {
            _users = users;
            _tokens = tokens;
            _access = access;
        }

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<IReadOnlyList<VUser>>> ListUsersAsync(CancellationToken cancellationToken)
        {
            if (!User.IsServerAdmin()) return StatusCode(StatusCodes.Status403Forbidden, AdminOnly);
            return Ok(await _users.ListUsersAsync(cancellationToken));
        }

        /// <summary>
        /// Creates a user with an initial password.
        /// </summary>
        /// <return>201 with the user, 400 for an invalid name or password, or 409 when a user or group has the name.</return>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<VUser>> CreateUserAsync([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
        {
            if (!User.IsServerAdmin()) return StatusCode(StatusCodes.Status403Forbidden, AdminOnly);

            string? nameError = PrincipalNames.Validate(request.Username);
            if (nameError is not null) return BadRequest(nameError);

            string? passwordError = PasswordHasher.Validate(request.Password);
            if (passwordError is not null) return BadRequest(passwordError);

            string username = request.Username.Trim();
            VUser? user = await _users.CreateUserAsync(username, PasswordHasher.Hash(request.Password), request.IsAdmin, request.DisplayName?.Trim(), request.Email?.Trim(), cancellationToken);
            if (user is null) return Conflict($"A user or group named '{username}' already exists.");
            return StatusCode(StatusCodes.Status201Created, user);
        }

        /// <summary>
        /// Disables or enables a user, or changes their server-admin flag. Takes effect on their next call.
        /// </summary>
        [HttpPatch]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<VUser>> UpdateUserAsync([FromQuery] string username, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
        {
            if (!User.IsServerAdmin()) return StatusCode(StatusCodes.Status403Forbidden, AdminOnly);

            VUser? existing = await _users.GetUserByNameAsync(username, cancellationToken);
            if (existing is null) return NotFound($"User '{username}' does not exist.");

            // Never leave the server without an admin who can log in: nobody could manage users again.
            bool losesAdmin = existing is { IsAdmin: true, Disabled: false } && (request.Disabled == true || request.IsAdmin == false);
            if (losesAdmin && await _users.CountActiveAdminsAsync(cancellationToken) <= 1)
                return Conflict($"'{existing.Username}' is the last server admin. Make someone else an admin first with 'vx user admin <name>'.");

            VUser? user = await _users.UpdateUserAsync(username, request.Disabled, request.IsAdmin, cancellationToken);
            if (user is null) return NotFound($"User '{username}' does not exist.");

            await _tokens.EvictUserSessionsAsync(user.Id, cancellationToken);
            if (request.IsAdmin is not null) await _access.InvalidateAllAsync();
            return Ok(user);
        }

        /// <summary>
        /// Changes the caller's own password and logs out their other devices.
        /// </summary>
        [HttpPut("me/password")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<bool>> ChangePasswordAsync([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
        {
            Guid userId = User.GetUserId();
            if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, await _users.GetPasswordHashAsync(userId, cancellationToken)))
                return BadRequest("The current password is incorrect.");

            string? passwordError = PasswordHasher.Validate(request.NewPassword);
            if (passwordError is not null) return BadRequest(passwordError);

            await _users.SetPasswordHashAsync(userId, PasswordHasher.Hash(request.NewPassword), cancellationToken);
            await _tokens.RevokeAllAsync(userId, User.GetTokenId(), cancellationToken);
            return Ok(true);
        }
    }
}
