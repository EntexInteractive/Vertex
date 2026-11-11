// Copyright 2026 Entex Interactive

using Vertex.Cli.Projects;

namespace Vertex.Cli.Branches
{
    public class BranchSettings : ProjectSettings
    {
        [CommandOption("--branch|-b")] public string? Branch { get; set; }
    }
}