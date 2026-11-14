// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;

namespace Vertex.Core.Data
{
    /// <summary>
    /// Represents a version control branch.
    /// </summary>
    public class VBranch
    {
        /// <summary>
        /// The branch unique identifier.
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// The time the branch was created.
        /// </summary>
        public UnixTime CreatedAt { get; } = UnixTime.Now;

        /// <summary>
        /// The branch name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The parent project.
        /// </summary>
        public Guid ProjectId { get; }

        /// <summary>
        /// The parent branch.
        /// </summary>
        public Guid? ParentId { get; set; }

        public VBranch(string name, Guid projectId, Guid? parentId = null)
        {
            Name = name;
            ProjectId = projectId;
            ParentId = parentId;
        }

        [JsonConstructor]
        public VBranch(Guid id, long createdAt, string name, Guid projectId, Guid? parentId)
        {
            Id = id;
            CreatedAt = UnixTime.FromMilliseconds(createdAt);
            Name = name;
            ProjectId = projectId;
            ParentId = parentId;
        }
    }
}