// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Newtonsoft.Json;

namespace Vertex.Core.Data
{
    public class VUser
    {
        /// <summary>
        /// The user's unique identifier.
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// The time the user was created.
        /// </summary>
        public UnixTime CreatedAt { get; } = UnixTime.Now;

        /// <summary>
        /// The username of the user.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// The user's current display name.
        /// </summary>
        [JsonIgnore]
        public string DisplayName => string.IsNullOrEmpty(Firstname) ? Username : Firstname;

        /// <summary>
        /// The user's password encoded as a SHA256 hash.
        /// </summary>
        [JsonIgnore]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// The user's firstname.
        /// </summary>
        public string Firstname { get; set; } = string.Empty;

        /// <summary>
        /// The user's lastname.
        /// </summary>
        public string Lastname { get; set; } = string.Empty;

        /// <summary>
        /// The user's last login time.
        /// </summary>
        public UnixTime LastLogin { get; set; } = UnixTime.Now;

        /// <summary>
        /// The user's linked email.
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// The user's permission level.
        /// </summary>
        public int Permission { get; set; } = 0;
    }
}