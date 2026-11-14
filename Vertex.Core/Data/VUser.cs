// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;

namespace Vertex.Core.Data
{
    /// <summary>
    /// A user account. Password hashes never leave the server, so they aren't part of this model.
    /// </summary>
    public class VUser
    {
        /// <summary>
        /// The user's unique identifier.
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// The time the user was created.
        /// </summary>
        public UnixTime CreatedAt { get; }

        /// <summary>
        /// The name the user logs in with. Unique, ignoring case, across users and groups.
        /// </summary>
        public string Username { get; }

        /// <summary>
        /// The name shown to other people. Falls back to <see cref="Username"/>.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// The user's email address, or empty.
        /// </summary>
        public string Email { get; }

        /// <summary>
        /// Server admins manage users and groups and have Admin access to every project.
        /// </summary>
        public bool IsAdmin { get; }

        /// <summary>
        /// Disabled users can't log in, and their existing tokens stop working.
        /// </summary>
        public bool Disabled { get; }

        /// <summary>
        /// The user's last successful login, or null if they never logged in.
        /// </summary>
        public UnixTime? LastLoginAt { get; }

        [JsonConstructor]
        public VUser(Guid id, long createdAt, string username, string? displayName, string? email, bool isAdmin, bool disabled, long? lastLoginAt)
        {
            Id = id;
            CreatedAt = UnixTime.FromMilliseconds(createdAt);
            Username = username;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName;
            Email = email ?? string.Empty;
            IsAdmin = isAdmin;
            Disabled = disabled;
            LastLoginAt = lastLoginAt is long ms ? UnixTime.FromMilliseconds(ms) : null;
        }
    }
}
