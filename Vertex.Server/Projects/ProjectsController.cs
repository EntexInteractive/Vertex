// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;
using Vertex.Core.Net.Responses;

namespace Vertex.Server.Projects
{
    [Route("api/v1/projects")]
    public class ProjectsController : Controller
    {
        private readonly ProjectsRepository _projects;

        public ProjectsController(ProjectsRepository projects)
        {
            _projects = projects;
        }

        /// <summary>
        /// Creates a project together with its default branch.
        /// </summary>
        /// <param name="name">The name of the project to create.</param>
        /// <param name="defaultBranch">The default branch's name. Defaults to <c>main</c>.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <return>201 with the project and branch, 400 for a missing name, or 409 when the name is taken.</return>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<CreateProjectResponse>> CreateProjectAsync([FromQuery] string name, [FromQuery] string? defaultBranch, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(name)) return BadRequest("A project name is required.");

            // TODO: use the authenticated user once authentication exists.
            Guid ownerId = Guid.NewGuid();
            VProject project = new(name.Trim(), ownerId);
            VBranch branch = new(string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch.Trim(), project.Id, ownerId);

            if (!await _projects.CreateProjectAsync(project, branch, cancellationToken)) return Conflict($"A project named '{project.Name}' already exists.");
            return StatusCode(StatusCodes.Status201Created, new CreateProjectResponse(project, branch));
        }

        /// <summary>
        /// Retrieves the list of available projects.
        /// </summary>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <return>A task that represents the asynchronous operation. The task result contains the collection of projects wrapped in an <see cref="ActionResult{TValue}"/>.</return>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IReadOnlyList<VProject>>> GetProjectsAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<VProject> projects = await _projects.ListProjectsAsync(cancellationToken);
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
            return Ok(project);
        }
    }
}