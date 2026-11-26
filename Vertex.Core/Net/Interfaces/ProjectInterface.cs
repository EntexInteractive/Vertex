// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public class ProjectInterface(string url, string? token = null) : BaseInterface(url, token)
    {
        /// <summary>
        /// Creates a project and its default branch. Fails with status 409 when the name is taken.
        /// </summary>
        /// <param name="upstream">A branch of another project ("UE5/release") the default branch takes its files from:
        /// linked, and its files copied onto the branch as the first revision. Fails with 404 or 403 when the caller can't
        /// read it, before anything is created.</param>
        /// <param name="upstreamReadOnly">Refuse submits to the upstream's files on the new branch.</param>
        public async Task<ServerResult<CreateProjectResponse>> CreateProjectAsync(string projectName, string defaultBranch = "main", CancellationToken cancellationToken = default,
            string? upstream = null, bool upstreamReadOnly = false)
        {
            string query = $"/api/v1/projects?name={Uri.EscapeDataString(projectName)}&defaultBranch={Uri.EscapeDataString(defaultBranch)}";
            if (!string.IsNullOrWhiteSpace(upstream))
            {
                (string project, string branch) = VUpstream.ParseName(upstream);
                query += $"&upstreamProject={Uri.EscapeDataString(project)}&upstreamBranch={Uri.EscapeDataString(branch)}&upstreamReadOnly={(upstreamReadOnly ? "true" : "false")}";
            }

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