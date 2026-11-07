// Copyright 2026 Entex Interactive

using Vertex.Core.Contexts;
using Vertex.Core.Data;

namespace Vertex.Core.IO
{
    public class FileScanner
    {
        public IReadOnlyList<VFile> GetFiles(VBranch branch, string directory)
        {
            List<VFile> files = new();
            IEnumerable<string> localFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
            foreach (string lf in localFiles)
            {
                files.Add(new VFile(lf, branch));
            }

            return files.ToArray();
        }

        public IReadOnlyList<VChange> GetChanges(ClientDbContext context, VBranch branch)
        {
            List<VChange> changes = new();
            IReadOnlyList<VFile> cacheFiles = context.Files.ToList();
            //IReadOnlyList<VFile> localFiles = GetFiles(branch);

            return changes;
        }
    }
}