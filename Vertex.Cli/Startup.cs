// Copyright 2026 Entex Interactive

using Spectre.Console.Cli;
using Vertex.Cli.Branches;
using Vertex.Cli.Files;
using Vertex.Cli.Net;
using Vertex.Cli.Profiles;
using Vertex.Cli.Projects;
using Vertex.Cli.Utils;
using Vertex.Cli.Workspaces;

namespace Vertex.Cli
{
    public static class Startup
    {
        public static void RegisterCommands(IConfigurator config)
        {
            config.AddCommand<ClearCommand>("clear").WithDescription("Clears the console window.");
            config.AddCommand<ClearCommand>("cls").WithDescription("Clears the console window.");

            config.AddCommand<InitCommand>("init").WithDescription("Creates a project on the server and makes this folder its workspace.");
            config.AddCommand<FetchCommand>("fetch").WithDescription("Links a folder to an existing project.");

            config.AddCommand<InfoCommand>("info").WithDescription("Displays server information.");
            config.AddCommand<PushCommand>("push");
            config.AddCommand<PullCommand>("pull");

            config.AddCommand<StatusCommand>("status").WithDescription("Shows files changed since the last push.");

            config.AddBranch("profile", branch =>
            {
                branch.AddCommand<CreateProfileCommand>("create").WithDescription("Creates a new profile.");
            });
            
            config.AddBranch("project", branch =>
            {
                branch.AddCommand<CreateProjectCommand>("create").WithDescription("Creates a new project.");
            });

            config.AddCommand<BranchCommand>("branch").WithDescription("Shows, lists, creates or deletes branches.")
                .WithExample("branch")
                .WithExample("branch", "list")
                .WithExample("branch", "feature-x")
                .WithExample("branch", "-d", "feature-x");
        }
    }
}
