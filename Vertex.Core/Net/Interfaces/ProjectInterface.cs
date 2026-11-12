// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public class ProjectInterface(string url) : BaseInterface(url)
    {
        /// <summary>
        /// Creates a project and its default branch. Fails with status 409 when the name is taken.
        /// </summary>
        public async Task<ServerResult<CreateProjectResponse>> CreateProjectAsync(string projectName, string defaultBranch = "main", CancellationToken cancellationToken = default)
        {
            string query = $"/api/v1/projects?name={Uri.EscapeDataString(projectName)}&defaultBranch={Uri.EscapeDataString(defaultBranch)}";
            return await SendAsync<CreateProjectResponse>(HttpMethod.Post, query, null, cancellationToken);
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