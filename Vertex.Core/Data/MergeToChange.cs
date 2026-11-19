// Copyright 2026 Entex Interactive

namespace Vertex.Core.Data
{
    /// <summary>
    /// The result of changing the branch a branch's revisions are merged into automatically.
    /// </summary>
    /// <param name="Branch">The branch, with its new target in <see cref="VBranch.MergeTo"/>.</param>
    /// <param name="Previous">The target it had before, or null for none.</param>
    public sealed record MergeToChange(VBranch Branch, Guid? Previous);
}
