// Copyright 2026 Entex Interactive

using Vertex.Core.Data;

namespace Vertex.Core.Net.Responses
{
    /// <summary>
    /// Returned by POST /api/v1/projects: the new project and the default branch created with it.
    /// </summary>
    /// <param name="Project">The new project.</param>
    /// <param name="DefaultBranch">Its default branch.</param>
    /// <param name="Upstream">With an upstream: the branch's link to it, or null when linking failed.</param>
    /// <param name="ImportRevision">The revision that copied the upstream's files onto the branch; 0 when it had none.</param>
    /// <param name="ImportedFiles">Files that revision added.</param>
    /// <param name="UpstreamError">Why the upstream couldn't be linked or imported. The project exists either way.</param>
    public sealed record CreateProjectResponse(VProject Project, VBranch DefaultBranch, VUpstream? Upstream = null, long ImportRevision = 0, int ImportedFiles = 0,
        string? UpstreamError = null);
}
