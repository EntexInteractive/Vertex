// Copyright 2026 Entex Interactive

namespace Vertex.Core.Security
{
    /// <summary>
    /// What a user may do in a project. Levels are ordered: each one includes everything below it.
    /// </summary>
    /// <remarks>Stored as <c>SMALLINT</c>; don't renumber.</remarks>
    public enum AccessLevel : short
    {
        /// <summary>
        /// No access.
        /// </summary>
        None = 0,

        /// <summary>
        /// Sync files and view history.
        /// </summary>
        Read = 1,

        /// <summary>
        /// Submit revisions and lock files.
        /// </summary>
        Write = 2,

        /// <summary>
        /// Manage the project's access and branches, and release anyone's locks in it. The highest level that can be
        /// granted: it applies to that one project only.
        /// </summary>
        Manage = 3,

        /// <summary>
        /// Server admin: everything in every project. Comes only from the user's server-admin flag
        /// (<c>vx user admin</c>), never from a grant.
        /// </summary>
        Admin = 4
    }

    /// <summary>
    /// Whether an access grant names a user or a group.
    /// </summary>
    /// <remarks>Stored as <c>SMALLINT</c>; don't renumber.</remarks>
    public enum PrincipalKind : short
    {
        User = 0,
        Group = 1
    }
}
