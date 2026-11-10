// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;

namespace Vertex.Core.Data
{
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
        /// The <see cref="VUser"/> that created the branch.
        /// </summary>
        public Guid OwnerId { get; set; }

        /// <summary>
        /// The parent branch.
        /// </summary>
        public Guid? ParentId { get; set; }

        public VBranch(string name, Guid projectId, Guid ownerId, Guid? parentId = null)
        {
            Name = name;
            ProjectId = projectId;
            OwnerId = ownerId;
            ParentId = parentId;
        }
    }
}