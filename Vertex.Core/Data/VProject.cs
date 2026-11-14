// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.Security;

namespace Vertex.Core.Data
{
    public class VProject
    {
        /// <summary>
        /// The project unique indentifer.
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// The time the project was created.
        /// </summary>
        public UnixTime CreatedAt { get; } = UnixTime.Now;

        /// <summary>
        /// The project name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Who can read the project without an explicit grant. New projects are internal: on-prem servers usually
        /// hold only the team's own people.
        /// </summary>
        public ProjectVisibility Visibility { get; set; } = ProjectVisibility.Internal;

        /// <summary>
        /// The <see cref="VUser"/> that created the project.
        /// </summary>
        public Guid OwnerId { get; set; }

        public VProject(string name, Guid ownerId)
        {
            Name = name;
            OwnerId = ownerId;
        }

        [JsonConstructor]
        public VProject(Guid id, long createdAt, string name, ProjectVisibility visibility, Guid ownerId)
        {
            Id = id;
            CreatedAt = UnixTime.FromMilliseconds(createdAt);
            Name = name;
            Visibility = visibility;
            OwnerId = ownerId;
        }
    }
}
