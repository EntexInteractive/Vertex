// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;
using Vertex.Core.Net.Responses;
using Vertex.Core.Security;
using Vertex.Server.Access;
using Vertex.Server.Auth;
using Vertex.Server.Branches;
using Vertex.Server.History;

namespace Vertex.Server.Projects
{
    [Route("api/v1/projects")]
    public class ProjectsController : Controller
    {
        private readonly ProjectsRepository _projects;
        private readonly AccessService _access;
        private readonly UpstreamLinks _links;
        private readonly UpstreamsRepository _upstreams;
        private readonly ILogger<ProjectsController> _logger;

        public ProjectsController(ProjectsRepository projects, AccessService access, UpstreamLinks links, UpstreamsRepository upstreams, ILogger<ProjectsController> logger)
        {
            _projects = projects;
            _access = access;
            _links = links;
            _upstreams = upstreams;
            _logger = logger;
        }

        /// <summary>
        /// Creates a project together with its default branch. Any logged-in user can create one, and becomes its
        /// admin. New projects are internal: every logged-in user can read them.
        /// </summary>
        /// <param name="name">The name of the project to create.</param>
        /// <param name="defaultBranch">The default branch's name. Defaults to <c>main</c>.</param>
        /// <param name="upstreamProject">With <paramref name="upstreamBranch"/>: a branch of another project the default
        /// branch takes its files from (a studio engine for a game). It is linked and its files copied onto the branch
        /// as the first revision (metadata only). The caller needs read access to it.</param>
        /// <param name="upstreamReadOnly">Refuse submits to the upstream's files on the new branch.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <return>201 with the project and branch, 400 for a missing name, 404 or 403 for an upstream the caller can't
        /// read, or 409 when the name is taken. When the project was created but its upstream couldn't be imported, 201
        /// with <see cref="CreateProjectResponse.UpstreamError"/>.</return>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<CreateProjectResponse>> CreateProjectAsync([FromQuery] string name, [FromQuery] string? defaultBranch, [FromQuery] string? upstreamProject = null,
            [FromQuery] string? upstreamBranch = null, [FromQuery] bool upstreamReadOnly = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name)) return BadRequest("A project name is required.");

            // Check the upstream first, so a typo creates nothing.
            ResolvedUpstream? upstream = null;
            if (!string.IsNullOrWhiteSpace(upstreamProject))
            {
                upstream = await _links.ResolveAsync(User, upstreamProject, upstreamBranch ?? "main", cancellationToken);
                if (upstream.Branch is null) return StatusCode(upstream.StatusCode, upstream.Error);
            }

            Guid ownerId = User.GetUserId();
            VProject project = new(name.Trim(), ownerId);
            VBranch branch = new(string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch.Trim(), project.Id);

            if (!await _projects.CreateProjectAsync(project, branch, cancellationToken)) return Conflict($"A project named '{project.Name}' already exists.");
            if (upstream?.Branch is null) return StatusCode(StatusCodes.Status201Created, new CreateProjectResponse(project, branch));

            // The project stands either way; a failure here is reported with the commands that finish the job.
            CreateProjectResponse response;
            try
            {
                VUpstream link = (await _upstreams.SetAsync(project.Id, branch.Id, upstream.Branch.Id, null, upstreamReadOnly, cancellationToken))!;
                MergeOutcome imported = await _links.ImportAsync(project.Id, branch.Id, link, ownerId, cancellationToken);
                response = imported.Status is MergeStatus.Merged or MergeStatus.UpToDate
                    ? new CreateProjectResponse(project, branch, link, imported.Revision ?? 0, imported.Files)
                    : new CreateProjectResponse(project, branch, link, UpstreamError: $"the merge ended as {imported.Status.ToString().ToLowerInvariant()}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Linking project {Project} to upstream {Upstream} failed", project.Name, $"{upstream.Project!.Name}/{upstream.Branch.Name}");
                response = new CreateProjectResponse(project, branch, UpstreamError: ex.Message);
            }

            return StatusCode(StatusCodes.Status201Created, response);
        }

        /// <summary>
        /// Retrieves the projects the caller can read.
        /// </summary>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <return>A task that represents the asynchronous operation. The task result contains the collection of projects wrapped in an <see cref="ActionResult{TValue}"/>.</return>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IReadOnlyList<VProject>>> GetProjectsAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<VProject> projects = await _projects.ListProjectsForUserAsync(User.GetUserId(), cancellationToken);
            return Ok(projects);
        }

        /// <summary>
        /// Retrieves the project that matches the specified identifier.
        /// </summary>
        /// <param name="id">The identifier of the project to retrieve.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <return>A task that represents the asynchronous operation. The task result contains the matching project wrapped in an <see cref="ActionResult{TValue}"/>, or a 404 Not Found response if no project exists with the specified identifier.</return>
        [HttpGet("by-id")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VProject>> GetProjectByIdAsync([FromQuery] Guid id, CancellationToken cancellationToken)
        {
            VProject? project = await _projects.GetProjectByIdAsync(id, cancellationToken);
            if (project is null) return NotFound();

            AccessCheck check = await _access.CheckAsync(User, project.Id, AccessLevel.Read, cancellationToken);
            if (!check.Allowed) return StatusCode(check.StatusCode, check.Message);
            return Ok(project);
        }

        /// <summary>
        /// Retrieves a project with the specified name.
        /// </summary>
        /// <param name="name">The name of the project to retrieve.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <return>A task that represents the asynchronous operation. The task result contains the matching project wrapped in an <see cref="ActionResult{TValue}"/>, or a 404 Not Found response if no project with the specified name exists.</return>
        [HttpGet("by-name")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VProject>> GetProjectByNameAsync([FromQuery] string name, CancellationToken cancellationToken)
        {
            VProject? project = await _projects.GetProjectByNameAsync(name, cancellationToken);
            if (project is null) return NotFound();

            AccessCheck check = await _access.CheckAsync(User, project.Id, AccessLevel.Read, cancellationToken);
            if (!check.Allowed) return StatusCode(check.StatusCode, check.Message);
            return Ok(project);
        }
    }
}