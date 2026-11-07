// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Vertex.Core.Data
{
    public class VBranch
    {
        /// <summary>
        /// The branch unique identifier.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// The time the branch was created.
        /// </summary>
        public UnixTime CreatedAt { get; }

        /// <summary>
        /// The branch name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The parent project.
        /// </summary>
        public VProject Project { get; }

        /// <summary>
        /// The parent branch.
        /// </summary>
        public VBranch Parent { get; }

        /// <summary>
        /// The branch's required permission index.
        /// </summary>
        public int Permission { get; set; } = 0;

        /// <summary>
        /// The <see cref="VUser"/> that created the branch.
        /// </summary>
        public VUser Owner { get; set; }
    }
}