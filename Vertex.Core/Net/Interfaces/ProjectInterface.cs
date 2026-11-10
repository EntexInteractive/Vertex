// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public class ProjectInterface(string url) : BaseInterface(url)
    {
        public async Task<ServerResult<VProject>> CreateProjectAsync(string projectName, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VProject>(HttpMethod.Post, $"/api/v1/projects?name={Uri.EscapeDataString(projectName)}", null, cancellationToken);
        }

        public async Task<ServerResult<IReadOnlyList<VProject>>> GetProjectsAsync(CancellationToken cancellationToken = default)
        {
            return await SendAsync<IReadOnlyList<VProject>>(HttpMethod.Get, "/api/v1/projects", null, cancellationToken);
        }

        public async Task<ServerResult<VProject>> GetProjectByIdAsync(string projectId, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VProject>(HttpMethod.Get, $"/api/v1/projects/by-id?id={Uri.EscapeDataString(projectId)}", null, cancellationToken);
        }

        public async Task<ServerResult<VProject>> GetProjectByNameAsync(string projectName, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VProject>(HttpMethod.Get, $"/api/v1/projects/by-name?name={Uri.EscapeDataString(projectName)}", null, cancellationToken);
        }
    }
}