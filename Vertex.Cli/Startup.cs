// Copyright 2026 Entex Interactive

using Spectre.Console.Cli;
using Vertex.Cli.Branches;
using Vertex.Cli.Files;
using Vertex.Cli.Net;
using Vertex.Cli.Projects;
using Vertex.Cli.Utils;

namespace Vertex.Cli
{
    public static class Startup
    {
        public static void RegisterCommands(IConfigurator config)
        {
            config.AddCommand<ClearCommand>("clear").WithDescription("Clears the console window.");
            config.AddCommand<ClearCommand>("cls").WithDescription("Clears the console window.");

            config.AddCommand<InfoCommand>("info").WithDescription("Displays server information.");
            config.AddCommand<PushCommand>("push");
            config.AddCommand<PullCommand>("pull");

            config.AddCommand<StatusCommand>("status").WithDescription("Displays the status of the local files.");

            config.AddBranch("project", branch =>
            {
                branch.AddCommand<CreateProjectCommand>("create");
            });

            config.AddBranch("branch", branch =>
            {
                branch.AddCommand<BranchesCommand>("create");
                branch.AddCommand<DeleteBranchCommand>("delete");
            });
        }
    }
}
