// Copyright 2026 Entex Interactive

using Vertex.Core.Data;

namespace Vertex.Server.Branches
{
    public class BranchesRepository
    {
        public async Task<bool> CreateBranchAsync(VBranch branch, CancellationToken cancellationToken = default)
        {
            return true;
        }

        public async Task<IReadOnlyList<VBranch>> ListBranchesAsync(Guid projectId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task<VBranch?> GetBranchByIdAsync(Guid projectId, string id, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task<VBranch?> GetBranchByNameAsync(Guid projectId, string name, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}