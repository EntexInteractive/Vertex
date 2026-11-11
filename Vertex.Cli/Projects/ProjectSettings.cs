// Copyright 2026 Entex Interactive

using Vertex.Cli.Net;

namespace Vertex.Cli.Projects;

public class ProjectSettings : NetworkSettings
{
    [Spectre.Console.Cli.CommandOption("--project|-p")]
    public string? Project { get; set; }
}