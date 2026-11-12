// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Branches;

namespace Vertex.Cli.Files
{
    public class FilesSettings : BranchSettings
    {
        [CommandOption("--force|-f")]
        [Description("Forces the operation, bypassing safeguards.")]
        public bool Force { get; set; }

        [CommandOption("--parallel")]
        [Description("The number of parallel operations to perform.")]
        public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
    }
}