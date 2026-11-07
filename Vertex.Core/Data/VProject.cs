// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;

namespace Vertex.Core.Data
{
    public class VProject
    {
        /// <summary>
        /// The project unique indentifer.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// The time the project was created.
        /// </summary>
        public UnixTime CreatedAt { get; }

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
        public VUser Owner { get; set; }
    }
}