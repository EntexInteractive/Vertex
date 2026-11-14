// Copyright 2026 Entex Interactive

namespace Vertex.Core.Security
{
    /// <summary>
    /// What a user may do in a project. Levels are ordered: each one includes everything below it.
    /// </summary>
    /// <remarks>Stored as <c>SMALLINT</c>; don't renumber.</remarks>
    public enum AccessLevel : short
    {
        /// <summary>No access.</summary>
        None = 0,

        /// <summary>Sync files and view history.</summary>
        Read = 1,

        /// <summary>Submit revisions and lock files.</summary>
        Write = 2,

        /// <summary>Manage access and branches, and delete the project.</summary>
        Admin = 3
    }

    /// <summary>
    /// Who can read a project without an explicit grant.
    /// </summary>
    /// <remarks>Stored as <c>SMALLINT</c>; don't renumber.</remarks>
    public enum ProjectVisibility : short
    {
        /// <summary>Only users and groups with a grant.</summary>
        Private = 0,

        /// <summary>Every logged-in user can read it.</summary>
        Internal = 1
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
