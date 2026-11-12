// Copyright 2026 Entex Interactive

using System.ComponentModel;
using Vertex.Cli.Net;

namespace Vertex.Cli.Projects
{
    public class ProjectSettings : NetworkSettings
    {
        [CommandOption("--project|-p")]
        [Description("The name of the project to use.")]
        public string? Project { get; set; }
    }
}