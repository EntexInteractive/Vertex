// Copyright 2026 Entex Interactive

using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Data;

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
        /// Creates a new project with the specified name and returns the created project with a 201 Created response.
        /// </summary>
        /// <param name="name">The name of the project to create.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <return>A task that represents the asynchronous operation. The task result contains the created project wrapped in an <see cref="ActionResult{TValue}"/>.</return>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<VProject>> CreateProjectAsync([FromQuery] string name, CancellationToken cancellationToken)
        {
            VProject project = new VProject(name, Guid.NewGuid());
            if (!await _projects.CreateProjectAsync(project, cancellationToken)) return BadRequest();
            return StatusCode(StatusCodes.Status201Created, project);
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
        public async Task<ActionResult<VProject>> GetProjectByIdAsync([FromQuery] string id, CancellationToken cancellationToken)
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