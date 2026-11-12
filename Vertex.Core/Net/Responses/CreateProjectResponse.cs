// Copyright 2026 Entex Interactive

using Vertex.Core.Data;

namespace Vertex.Core.Net.Responses
{
    /// <summary>
    /// Returned by POST /api/v1/projects: the new project and the default branch created with it.
    /// </summary>
    public sealed record CreateProjectResponse(VProject Project, VBranch DefaultBranch);
}