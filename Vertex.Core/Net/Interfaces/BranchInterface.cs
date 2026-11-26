// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public class BranchInterface(string url, string? token = null) : BaseInterface(url, token)
    {
        /// <summary>
        /// Creates a branch: a copy of <paramref name="parentId"/>'s files, or empty without one. With
        /// <paramref name="mergeToId"/>, every revision on it is merged on into that branch.
        /// </summary>
        public async Task<ServerResult<VBranch>> CreateBranchAsync(Guid projectId, string branchName, Guid? parentId = null, CancellationToken cancellationToken = default,
            Guid? mergeToId = null)
        {
            string query = $"/api/v1/branches?projectId={projectId}&name={Uri.EscapeDataString(branchName)}";
            if (parentId.HasValue) query += $"&parentId={parentId.Value}";
            if (mergeToId.HasValue) query += $"&mergeToId={mergeToId.Value}";

            return await SendAsync<VBranch>(HttpMethod.Post, query, null, cancellationToken);
        }

        /// <summary>
        /// Sets the branch <paramref name="branchId"/>'s revisions are merged into automatically, or with null stops
        /// it. Fails with 400 (and a one-line reason) for a target that's missing, unrelated or would make a loop.
        /// </summary>
        public async Task<ServerResult<MergeToChange>> SetMergeToAsync(Guid projectId, Guid branchId, Guid? mergeToId, CancellationToken cancellationToken = default)
        {
            string query = $"/api/v1/branches/merge-to?projectId={projectId}&branchId={branchId}";
            if (mergeToId.HasValue) query += $"&mergeToId={mergeToId.Value}";
            return await SendAsync<MergeToChange>(HttpMethod.Put, query, null, cancellationToken);
        }

        public async Task<ServerResult<VBranch>> GetBranchByIdAsync(Guid projectId, Guid branchId, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VBranch>(HttpMethod.Get, $"/api/v1/branches/by-id?projectId={projectId}&branchId={branchId}", null, cancellationToken);
        }

        /// <summary>
        /// Deletes a branch by name. Fails with 404 when it doesn't exist and 409 when other branches were
        /// created from it.
        /// </summary>
        public async Task<ServerResult<VBranch>> DeleteBranchAsync(Guid projectId, string branchName, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VBranch>(HttpMethod.Delete, $"/api/v1/branches?projectId={projectId}&name={Uri.EscapeDataString(branchName)}", null, cancellationToken);
        }

        /// <summary>
        /// Lists a project's branches, ordered by name.
        /// </summary>
        public async Task<ServerResult<IReadOnlyList<VBranch>>> ListBranchesAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            return await SendAsync<IReadOnlyList<VBranch>>(HttpMethod.Get, $"/api/v1/branches?projectId={projectId}", null, cancellationToken);
        }

        /// <summary>
        /// The branch's upstreams (at most one for now) and how far behind it is. With <paramref name="differences"/>,
        /// also how many of the upstream's files differ (the server compares both file lists).
        /// </summary>
        public async Task<ServerResult<IReadOnlyList<VUpstream>>> GetUpstreamsAsync(Guid projectId, Guid branchId, bool differences = false, CancellationToken cancellationToken = default)
        {
            string query = $"/api/v1/branches/upstream?projectId={projectId}&branchId={branchId}&differences={(differences ? "true" : "false")}";
            return await SendAsync<IReadOnlyList<VUpstream>>(HttpMethod.Get, query, null, cancellationToken);
        }

        /// <summary>
        /// Makes <paramref name="upstreamProject"/>/<paramref name="upstreamBranch"/> the branch's upstream, or changes the
        /// link's settings. Fails with 404 (no such project or branch), 403 (no read access to it) or 400 (same project,
        /// or a loop), each with a one-line reason.
        /// </summary>
        /// <param name="paths">The folders and files the upstream owns, comma-separated, or "auto" for its top-level ones;
        /// null keeps the link's.</param>
        /// <param name="readOnly">Refuse submits to those paths on the branch; null keeps the link's setting.</param>
        public async Task<ServerResult<VUpstream>> SetUpstreamAsync(Guid projectId, Guid branchId, string upstreamProject, string upstreamBranch, string? paths = null,
            bool? readOnly = null, CancellationToken cancellationToken = default)
        {
            string query = $"/api/v1/branches/upstream?projectId={projectId}&branchId={branchId}&upstreamProject={Uri.EscapeDataString(upstreamProject)}" +
                           $"&upstreamBranch={Uri.EscapeDataString(upstreamBranch)}";
            if (paths is not null) query += $"&paths={Uri.EscapeDataString(paths)}";
            if (readOnly is bool value) query += $"&readOnly={(value ? "true" : "false")}";
            return await SendAsync<VUpstream>(HttpMethod.Put, query, null, cancellationToken);
        }

        /// <summary>
        /// Unlinks the branch from its upstream; its files stay.
        /// </summary>
        /// <returns>The links removed (empty when it had none).</returns>
        public async Task<ServerResult<IReadOnlyList<VUpstream>>> ClearUpstreamAsync(Guid projectId, Guid branchId, CancellationToken cancellationToken = default)
        {
            return await SendAsync<IReadOnlyList<VUpstream>>(HttpMethod.Delete, $"/api/v1/branches/upstream?projectId={projectId}&branchId={branchId}", null, cancellationToken);
        }

        public async Task<ServerResult<VBranch>> GetBranchByNameAsync(Guid projectId, string branchName, CancellationToken cancellationToken = default)
        {
            return await SendAsync<VBranch>(HttpMethod.Get, $"/api/v1/branches/by-name?projectId={projectId}&name={Uri.EscapeDataString(branchName)}", null, cancellationToken);
        }
    }
}