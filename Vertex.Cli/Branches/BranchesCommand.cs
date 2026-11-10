// Copyright 2026 Entex Interactive

using Newtonsoft.Json.Linq;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Branches
{
    public class BranchesCommand : BaseBranchOptions
    {
        private readonly Argument<string> _nameArg = new("name") { Description = "Name of the branch to create." };

        private readonly Command _createCmd = new Command("create", "Creates a new branch.");

        public BranchesCommand() : base("branches", "Manages project branches.")
        {
            _createCmd.SetAction(HandleCreateCommandAsync);
            _createCmd.Add(_nameArg);
            this.Add(_createCmd);

            this.SetAction(HandleCommandAsync);
        }

        private async Task HandleCreateCommandAsync(ParseResult args)
        {
            string serverAddress = args.GetValue<string>(ServerOption) ?? "https://localhost:5000";

            string projectName = args.GetValue(ProjectOption) ?? "Vertex";
            using ProjectInterface projectInterface = new(serverAddress);
            ServerResult<VProject> projectResult = await projectInterface.GetProjectByNameAsync(projectName);
            if (!projectResult.IsSuccess)
            {
                Console.WriteLine($"Failed to get project: {projectResult.Exception?.Message}");
                return;
            }

            VProject project = projectResult.Data!;
            Console.WriteLine(JObject.FromObject(projectResult));

            string branchName = args.GetValue(BranchOption) ?? "main";
            using BranchInterface branchInterface = new(serverAddress);
            ServerResult<VBranch> result = await branchInterface.CreateBranchAsync(project.Id, branchName);
            if (result.IsSuccess)
            {
                Console.WriteLine($"Branch '{result.Data?.Name}' created successfully.");
            }
            else
            {
                Console.WriteLine($"Failed to create branch: {result.Exception?.Message}");
            }
        }

        private Task HandleCommandAsync(ParseResult args)
        {
            throw new NotImplementedException();
        }
    }
}