// Copyright 2026 Entex Interactive

namespace Vertex.Cli.Utils
{
    public class ClearCommand : Command
    {
        public override int Execute(CommandContext context, CancellationToken cancellationToken)
        {
            AnsiConsole.Clear();
            return 0;
        }
    }
}