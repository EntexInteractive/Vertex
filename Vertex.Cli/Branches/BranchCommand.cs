// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Newtonsoft.Json.Linq;
using Vertex.Cli.Files;
using Vertex.Cli.Projects;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Branches
{
    public class BranchCommand : BaseFilesCommand
    {
        private readonly Command CreateCmd = new Command("create", "Creates a new branch.");
        private readonly Command DeleteCmd = new Command("delete", "Deletes a branch.");

        public BranchCommand() : base("branch", "Manages project branches.")
        {
            CreateCmd.SetAction(HandleCreateAsync);
            CreateCmd.Add(ServerOption);
            CreateCmd.Add(ProjectOption);
            CreateCmd.Add(BranchOption);
            Add(CreateCmd);

            DeleteCmd.SetAction(HandleDeleteAsync);
            DeleteCmd.Add(ServerOption);
            DeleteCmd.Add(ProjectOption);
            DeleteCmd.Add(BranchOption);
            Add(DeleteCmd);
        }

        private async Task HandleCreateAsync(ParseResult args)
        {
            string serverAddress = args.GetValue(ServerOption) ?? "https://localhost:5000";
            string relativePath = args.GetValue(PathOption) ?? Directory.GetCurrentDirectory();
            VConfig? config = VConfig.Parse(Path.Combine(relativePath, ".vxconfig"));

            string? projectName = args.GetValue(ProjectOption) ?? config?.Project;
            if (string.IsNullOrWhiteSpace(projectName))
            {
                Terminal.Error("Project name is required.");
                return;
            }
            
            using ProjectInterface projectInterface = new(serverAddress);
            ServerResult<VProject> projectResult = await projectInterface.GetProjectByNameAsync(projectName);
            if (!projectResult.IsSuccessStatusCode && projectResult.Data == null)
            {
                Terminal.Error($"Failed to get project: {projectResult.Exception?.Message ?? "Unknown error"}");
                return;
            }

            VProject project = projectResult.Data!;
            Console.WriteLine(JObject.FromObject(projectResult));

            string branchName = args.GetValue(BranchOption) ?? "main";
            using BranchInterface branchInterface = new(serverAddress);
            ServerResult<VBranch> result = await branchInterface.CreateBranchAsync(project.Id, branchName);
            if (!result.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to create branch: {result.Exception?.Message}");
                return;
            }

            Console.WriteLine($"Branch '{result.Data?.Name}' created successfully.");
        }

        private Task HandleDeleteAsync(ParseResult arg)
        {
            throw new NotImplementedException();
        }
    }
}