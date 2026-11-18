// Copyright 2026 Entex Interactive

namespace Vertex.Mirror
{
    /// <summary>
    /// The git command line, set up for unattended use: it never prompts for credentials (it fails instead) and never
    /// converts line endings or runs filters, so working tree bytes are exactly the repository's.
    /// </summary>
    public static class Git
    {
        public static readonly ExternalTool Tool = new("git", "Install it from https://git-scm.com so that 'git --version' works, then run this again.",
            ["-c", "core.autocrlf=false", "-c", "core.safecrlf=false"],
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });

        public static Task<string> RunAsync(string? workingDirectory, CancellationToken ct, params string[] args)
        {
            return Tool.RunAsync(workingDirectory, ct, args);
        }

        /// <summary>
        /// Turns off every content conversion in a clone, whatever the repository's .gitattributes say (text=auto,
        /// eol, LFS and other filters), so checkout writes and add stores the exact bytes. Call before checking out.
        /// </summary>
        public static void DisableConversions(string clone)
        {
            string info = Path.Combine(clone, ".git", "info");
            Directory.CreateDirectory(info);
            File.WriteAllText(Path.Combine(info, "attributes"), "* -text -filter -ident -working-tree-encoding\n");
        }
    }
}
