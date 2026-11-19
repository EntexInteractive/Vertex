// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;
using Vertex.Gui.Hosting;

namespace Vertex.Gui.Controllers
{
    public sealed record ProjectDto(Guid Id, string Name);

    /// <param name="ParentId">The branch this one was created from, or null for the project's first branch.</param>
    /// <param name="MergeTo">The branch this one merges into by default, or null.</param>
    public sealed record BranchDto(Guid Id, string Name, Guid? ParentId, Guid? MergeTo);

    /// <param name="Number">The revision number, shown as <c>#1234</c>.</param>
    /// <param name="CreatedAt">When it was submitted, in Unix milliseconds.</param>
    /// <param name="Files">How many files it changed.</param>
    public sealed record RevisionDto(long Number, string Message, string Username, string? DisplayName, long CreatedAt, int Files);

    /// <param name="Deleted">True when the revision deleted the file.</param>
    public sealed record RevisionFileDto(string Path, bool Deleted);

    /// <summary>
    /// Projects, branches and revisions on the server the app is logged in to.
    /// </summary>
    [ApiController]
    [Route("api/projects")]
    public sealed class ProjectsController(ServerSession session) : ControllerBase
    {
        private const int PageSize = 1000;

        /// <summary>
        /// The projects the logged-in user can see, by name.
        /// </summary>
        [HttpGet]
        public async Task<IReadOnlyList<ProjectDto>> List(CancellationToken cancellationToken)
        {
            using ProjectInterface projects = session.Connect((server, token) => new ProjectInterface(server, token));
            IReadOnlyList<VProject> result = Unwrap(await projects.GetProjectsAsync(cancellationToken), "list projects");
            return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new ProjectDto(p.Id, p.Name)).ToList();
        }

        /// <summary>
        /// The project's branches, by name.
        /// </summary>
        [HttpGet("{projectId:guid}/branches")]
        public async Task<IReadOnlyList<BranchDto>> Branches(Guid projectId, CancellationToken cancellationToken)
        {
            using BranchInterface branches = session.Connect((server, token) => new BranchInterface(server, token));
            IReadOnlyList<VBranch> result = Unwrap(await branches.ListBranchesAsync(projectId, cancellationToken), "list branches");
            return result.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Select(b => new BranchDto(b.Id, b.Name, b.ParentId, b.MergeTo)).ToList();
        }

        /// <summary>
        /// The branch's latest revisions, newest first.
        /// </summary>
        /// <param name="limit">How many to return, 1 to 1,000.</param>
        [HttpGet("{projectId:guid}/branches/{branchId:guid}/revisions")]
        public async Task<IReadOnlyList<RevisionDto>> Revisions(Guid projectId, Guid branchId, [FromQuery] int limit = 100, CancellationToken cancellationToken = default)
        {
            limit = Math.Clamp(limit, 1, PageSize);
            using RevisionInterface revisions = session.Connect((server, token) => new RevisionInterface(server, token));

            // The server lists oldest first from a starting revision, so page to the end and keep the last ones.
            // TODO: add a newest-first query to GetRevisions so this is one call on long histories.
            Queue<RevisionSummary> latest = new(limit + 1);
            long after = 0;
            while (true)
            {
                IReadOnlyList<RevisionSummary> page = await revisions.GetRevisionsAsync(projectId, branchId, after, PageSize, cancellationToken);
                foreach (RevisionSummary revision in page)
                {
                    latest.Enqueue(revision);
                    if (latest.Count > limit) latest.Dequeue();
                }

                if (page.Count < PageSize) break;
                after = page[^1].Number;
            }

            return latest.Reverse()
                .Select(r => new RevisionDto(r.Number, r.Message, r.Username, r.DisplayName, r.CreatedAt.ToUnixTimeMilliseconds(), r.Files))
                .ToList();
        }

        /// <summary>
        /// The files one revision changed, by path.
        /// </summary>
        [HttpGet("{projectId:guid}/branches/{branchId:guid}/revisions/{number:long}/files")]
        public async Task<IReadOnlyList<RevisionFileDto>> RevisionFiles(Guid projectId, Guid branchId, long number, CancellationToken cancellationToken)
        {
            using RevisionInterface revisions = session.Connect((server, token) => new RevisionInterface(server, token));
            IReadOnlyList<RevisionChange> changes = await revisions.GetRevisionChangesAsync(projectId, branchId, number, cancellationToken);
            return changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Select(c => new RevisionFileDto(c.Path, c.FileId is null)).ToList();
        }

        private static T Unwrap<T>(ServerResult<T> result, string action)
        {
            if (result.IsSuccessStatusCode && result.Data is not null) return result.Data;
            if (result.Exception is not null && result.StatusCode == 0) throw result.Exception;
            string reason = string.IsNullOrWhiteSpace(result.Exception?.Message) ? $"The server returned {result.StatusCode}." : result.Exception.Message;
            throw new HostProblemException($"Couldn't {action}: {reason}", result.StatusCode is >= 400 and < 500 ? result.StatusCode : StatusCodes.Status502BadGateway);
        }
    }
}
