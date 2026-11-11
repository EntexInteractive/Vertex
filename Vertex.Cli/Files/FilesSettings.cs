// Copyright 2026 Entex Interactive

using Vertex.Cli.Branches;

namespace Vertex.Cli.Files
{
    public class FilesSettings : BranchSettings
    {
        [CommandOption("--force|-f")] public bool Force { get; set; }

        [CommandOption("--path")] public string? Path { get; set; }
    }
}