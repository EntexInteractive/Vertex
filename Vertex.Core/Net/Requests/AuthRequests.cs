// Copyright 2026 Entex Interactive

using Vertex.Core.Security;

namespace Vertex.Core.Net.Requests
{
    /// <summary>
    /// Body of POST /api/v1/auth/login. <see cref="DeviceName"/> labels the token, e.g. the machine name.
    /// </summary>
    public sealed record LoginRequest(string Username, string Password, string DeviceName);

    /// <summary>
    /// Body of POST /api/v1/users.
    /// </summary>
    public sealed record CreateUserRequest(string Username, string Password, bool IsAdmin = false, string? DisplayName = null, string? Email = null);

    /// <summary>
    /// Body of PATCH /api/v1/users. Null fields are left unchanged.
    /// </summary>
    public sealed record UpdateUserRequest(bool? Disabled = null, bool? IsAdmin = null);

    /// <summary>
    /// Body of PUT /api/v1/users/me/password.
    /// </summary>
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    /// <summary>
    /// Body of PUT /api/v1/access: gives a user or group a level in a project, replacing any previous level.
    /// </summary>
    public sealed record GrantAccessRequest(string Name, AccessLevel Level);
}
