// Copyright 2026 Entex Interactive

using Vertex.Core.Security;

namespace Vertex.Core.Data
{
    /// <summary>
    /// One user's or group's access level in a project.
    /// </summary>
    public sealed record VAccessGrant(Guid PrincipalId, PrincipalKind Kind, string Name, AccessLevel Level);
}
