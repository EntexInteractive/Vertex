// Copyright 2026 Entex Interactive

using Spectre.Console.Cli;
using Vertex.Cli.Access;
using Vertex.Cli.Auth;
using Vertex.Cli.Branches;
using Vertex.Cli.Config;
using Vertex.Cli.Files;
using Vertex.Cli.Groups;
using Vertex.Cli.Net;
using Vertex.Cli.Projects;
using Vertex.Cli.Users;
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

            config.AddCommand<LoginCommand>("login").WithDescription("Logs in to a server and saves the login for later commands.")
                .WithExample("login")
                .WithExample("login", "https://vertex.example.com", "-u", "alice")
                .WithExample("login", "-u", "ci-bot", "--password-stdin");
            config.AddCommand<ConfigCommand>("config").WithDescription("Shows or changes settings for every command on this machine, such as the default server.")
                .WithExample("config")
                .WithExample("config", "server", "https://vertex.example.com")
                .WithExample("config", "server", "--unset");
            config.AddCommand<LogoutCommand>("logout").WithDescription("Logs out of a server and forgets the saved login.");
            config.AddCommand<PasswdCommand>("passwd").WithDescription("Changes your password.");

            config.AddBranch("user", branch =>
            {
                branch.SetDescription("Manages users (server admins only).");
                branch.AddCommand<AddUserCommand>("add").WithDescription("Adds a user with an initial password.");
                branch.AddCommand<ListUsersCommand>("list").WithDescription("Lists all users.");
                branch.AddCommand<DisableUserCommand>("disable").WithDescription("Stops a user from logging in and ends their logins.");
                branch.AddCommand<EnableUserCommand>("enable").WithDescription("Lets a disabled user log in again.");
                branch.AddCommand<UserAdminCommand>("admin").WithDescription("Makes a user a server admin, or removes it with --remove.");
            });

            config.AddBranch("group", branch =>
            {
                branch.SetDescription("Manages groups of users. Access granted to a group applies to all its members.");
                branch.AddCommand<CreateGroupCommand>("create").WithDescription("Creates a group.");
                branch.AddCommand<DeleteGroupCommand>("delete").WithDescription("Deletes a group and the access it gave.");
                branch.AddCommand<ListGroupsCommand>("list").WithDescription("Lists groups, or one group's members.");
                branch.AddCommand<AddGroupMembersCommand>("add").WithDescription("Adds users to a group.").WithExample("group", "add", "Artists", "alice", "bob");
                branch.AddCommand<RemoveGroupMembersCommand>("remove").WithDescription("Removes users from a group.");
            });

            config.AddBranch<AccessSettings>("access", branch =>
            {
                branch.SetDescription("Shows or changes who can read, write or administer the project.");
                branch.SetDefaultCommand<ListAccessCommand>();
                branch.AddCommand<GrantAccessCommand>("grant").WithDescription("Gives a user or group read, write or admin access.")
                    .WithExample("access", "grant", "Artists", "write");
                branch.AddCommand<RevokeAccessCommand>("revoke").WithDescription("Removes a user's or group's access.");
                branch.AddCommand<SetVisibilityCommand>("visibility").WithDescription("Makes the project internal or private.")
                    .WithExample("access", "visibility", "private");
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
