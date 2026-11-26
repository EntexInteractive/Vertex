// Copyright 2026 Entex Interactive

namespace Vertex.Core.Data
{
    /// <summary>
    /// A branch's upstream: a branch of another project it takes its files from, the way a game takes a studio engine.
    /// The branch holds its own copy of the upstream's files, takes new upstream revisions with <c>vx merge upstream</c>
    /// and sends its own changes back with <c>vx merge --into upstream</c>.
    /// </summary>
    /// <param name="BranchId">The downstream branch.</param>
    /// <param name="UpstreamProjectId">The upstream's project.</param>
    /// <param name="UpstreamProject">The upstream project's name.</param>
    /// <param name="UpstreamBranchId">The upstream branch.</param>
    /// <param name="UpstreamBranch">The upstream branch's name.</param>
    /// <param name="Paths">What the upstream owns: folder prefixes ending in '/' (<c>Engine/</c>) or files. A merge back
    /// up only sends these. Null means the upstream's current top-level folders and files.</param>
    /// <param name="ReadOnly">Submits to the upstream's paths are refused on the branch: changes go to the upstream first.</param>
    /// <param name="TakenRevision">The upstream revision the branch has taken in, or null before the first merge.</param>
    /// <param name="UpstreamHead">The upstream's latest revision.</param>
    /// <param name="Behind">Upstream revisions after <paramref name="TakenRevision"/>, which the branch hasn't taken in.</param>
    /// <param name="Differing">Files under <paramref name="Paths"/> whose content differs between the branch and the
    /// upstream's latest: the branch's own changes not sent up yet, plus the upstream's not taken in yet.</param>
    public sealed record VUpstream(Guid BranchId, Guid UpstreamProjectId, string UpstreamProject, Guid UpstreamBranchId, string UpstreamBranch, string[]? Paths, bool ReadOnly,
        long? TakenRevision = null, long UpstreamHead = 0, int Behind = 0, int Differing = 0)
    {
        /// <summary>"UE5/release".</summary>
        public string Name => $"{UpstreamProject}/{UpstreamBranch}";

        /// <summary>
        /// Splits "UE5/release" into its project and branch at the first '/' (branch names may contain more); a bare
        /// "UE5" means its <c>main</c> branch.
        /// </summary>
        public static (string Project, string Branch) ParseName(string name)
        {
            name = name.Trim();
            int slash = name.IndexOf('/');
            return slash < 0 ? (name, "main") : (name[..slash].Trim(), name[(slash + 1)..].Trim());
        }
    }
}
