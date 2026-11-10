// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;

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
        /// The project's permission index.
        /// </summary>
        public int Permission { get; set; } = 0;

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
        public VProject(Guid id, long createdAt, string name, int permission, Guid ownerId)
        {
            Id = id;
            CreatedAt = UnixTime.FromMilliseconds(createdAt);
            Name = name;
            Permission = permission;
            OwnerId = ownerId;
        }
    }
}