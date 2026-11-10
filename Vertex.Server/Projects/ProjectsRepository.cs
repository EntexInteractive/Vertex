// Copyright 2026 Entex Interactive

using Vertex.Core.Data;

namespace Vertex.Server.Projects
{
    public class ProjectsRepository
    {
        public async Task<VProject> CreateProjectAsync(string name, CancellationToken cancellationToken)
        {
            return new VProject
            {
                Name = name
            };
        }

        public async Task<IReadOnlyList<VProject>> GetProjectsAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task<VProject?> GetProjectByIdAsync(string id, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task<VProject?> GetProjectByNameAsync(string name, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}