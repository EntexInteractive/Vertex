// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;
using Vertex.Core.Net.Requests;
using Vertex.Core.Net.Responses;
using Vertex.Server.Users;

namespace Vertex.Server.Auth
{
    [Route("api/v1/auth")]
    public class AuthController : Controller
    {
        private readonly UsersRepository _users;
        private readonly TokensRepository _tokens;
        private readonly LoginThrottle _throttle;

        public AuthController(UsersRepository users, TokensRepository tokens, LoginThrottle throttle)
        {
            _users = users;
            _tokens = tokens;
            _throttle = throttle;
        }

        /// <summary>
        /// Exchanges a username and password for a token, which the client sends as <c>Authorization: Bearer</c>.
        /// </summary>
        /// <return>200 with the token, 401 for wrong credentials, or 429 after too many failed attempts.</return>
        [HttpPost("login"), AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<LoginResponse>> LoginAsync([FromBody] LoginRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password)) return BadRequest("A username and password are required.");

            string username = request.Username.Trim();
            if (_throttle.IsBlocked(username))
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, $"Too many failed logins for '{username}'. Wait {LoginThrottle.Window.TotalMinutes:N0} minutes and try again.");
            }

            UserCredentials? credentials = await _users.GetCredentialsAsync(username, cancellationToken);
            if (!PasswordHasher.Verify(request.Password, credentials?.PasswordHash) || credentials is null)
            {
                _throttle.RecordFailure(username);
                return Unauthorized("Incorrect username or password.");
            }

            if (credentials.Disabled) return Unauthorized($"The account '{username}' is disabled. Ask a server admin to run 'vx user enable {username}'.");

            _throttle.RecordSuccess(username);
            string device = string.IsNullOrWhiteSpace(request.DeviceName) ? "unknown device" : request.DeviceName.Trim();
            string token = await _tokens.CreateAsync(credentials.Id, device, cancellationToken);
            await _users.SetLastLoginAsync(credentials.Id, cancellationToken);

            VUser user = (await _users.GetUserByIdAsync(credentials.Id, cancellationToken))!;
            return Ok(new LoginResponse(token, user));
        }

        /// <summary>
        /// Revokes the token used for this call.
        /// </summary>
        [HttpDelete("token")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<bool>> LogoutAsync(CancellationToken cancellationToken)
        {
            await _tokens.RevokeAsync(User.GetTokenId(), cancellationToken);
            return Ok(true);
        }

        /// <summary>
        /// The user the token belongs to.
        /// </summary>
        [HttpGet("me")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<VUser>> GetCurrentUserAsync(CancellationToken cancellationToken)
        {
            VUser? user = await _users.GetUserByIdAsync(User.GetUserId(), cancellationToken);
            return user is null ? Unauthorized() : Ok(user);
        }
    }
}
