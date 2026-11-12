// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Projects;

namespace Vertex.Cli.Branches
{
    public class BranchSettings : ProjectSettings
    {
        [CommandOption("--branch|-b")]
        [Description("The name of the branch to use.")]
        public string? Branch { get; set; }

        [CommandOption("--path")]
        [Description("The path to a file or directory.")]
        public string? Path { get; set; } = Directory.GetCurrentDirectory();
    }
}