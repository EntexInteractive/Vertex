// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;

namespace Vertex.Core.Data
{
    public class VFile
    {
        public string Id { get; }

        /// <summary>
        /// The name of the file.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The universal path of the file.
        /// </summary>
        public string Fullname { get; set; }

        /// <summary>
        /// The time the file was last written to.
        /// </summary>
        public UnixTime LastWrite { get; set; }

        /// <summary>
        /// The branch of the file.
        /// </summary>
        public VBranch Branch { get; set; }

        /// <summary>
        /// The size, in bytes, of the file.
        /// </summary>
        public long PhysicalSize { get; set; }

        /// <summary>
        /// The user that currently has the file locked. Null if unlocked.
        /// </summary>
        public VUser? LockedBy { get; set; }

        public VFile(string path, VBranch branch)
        {
            
        }
    }
}