// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;

namespace Vertex.Core.Data
{
    /// <summary>
    /// A named set of users. Project access granted to a group applies to all of its members.
    /// </summary>
    public class VGroup
    {
        public Guid Id { get; }

        public UnixTime CreatedAt { get; }

        /// <summary>
        /// Unique, ignoring case, across users and groups, so a grant can name either without saying which.
        /// </summary>
        public string Name { get; }

        public int MemberCount { get; }

        [JsonConstructor]
        public VGroup(Guid id, long createdAt, string name, long memberCount)
        {
            Id = id;
            CreatedAt = UnixTime.FromMilliseconds(createdAt);
            Name = name;
            MemberCount = (int)memberCount;
        }
    }
}
