// Copyright 2026 Entex Interactive

using Vertex.Core.Data;

namespace Vertex.Server.Projects
{
    public class ProjectsRepository
    {
        public async Task<bool> CreateProjectAsync(VProject project, CancellationToken cancellationToken)
        {
            return true;
        }

        public async Task<IReadOnlyList<VProject>> ListProjectsAsync(CancellationToken cancellationToken)
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