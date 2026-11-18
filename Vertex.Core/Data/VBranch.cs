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

        /// <summary>
        /// The parent's latest revision when the branch was made from it: the branch started with the parent's files
        /// as of that revision. 0 for a branch that started empty.
        /// </summary>
        public long BaseRevision { get; set; }

        public VBranch(string name, Guid projectId, Guid? parentId = null)
        {
            Name = name;
            ProjectId = projectId;
            ParentId = parentId;
        }

        [JsonConstructor]
        public VBranch(Guid id, long createdAt, string name, Guid projectId, Guid? parentId, long baseRevision = 0)
        {
            BaseRevision = baseRevision;
            Id = id;
            CreatedAt = UnixTime.FromMilliseconds(createdAt);
            Name = name;
            ProjectId = projectId;
            ParentId = parentId;
        }
    }
}