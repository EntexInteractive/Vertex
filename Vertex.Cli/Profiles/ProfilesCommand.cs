// Copyright 2026 Entex Interactive

using Vertex.Cli.Branches;
using Vertex.Cli.Net;

namespace Vertex.Cli.Profiles
{
    public sealed class CreateProfileSettings : BranchSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; set; } = string.Empty;
    }

    public sealed class CreateProfileCommand : AsyncCommand<CreateProfileSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, CreateProfileSettings settings, CancellationToken cancellationToken)
        {


            AnsiConsole.MarkupLine(Markup.Escape(settings.Name));
            return 1;
        }
    }
}