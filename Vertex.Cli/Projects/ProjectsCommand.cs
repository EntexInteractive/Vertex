// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Projects
{
    public class ProjectsCommand : BaseProjectOptions
    {
        private readonly Argument<string> _nameArg = new("name") { Description = "Name of the branch to create." };

        private readonly Command _createCmd = new Command("create", "Creates a new branch.");

        public ProjectsCommand() : base("projects", "Manages projects.")
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
            ServerResult<VProject> result = await projectInterface.CreateProjectAsync(projectName);
            if (result.IsSuccess)
            {
                Console.WriteLine($"Project '{result.Data?.Name}' created successfully.");
            }
            else
            {
                Console.WriteLine($"Failed to create project: {result.Exception?.Message}");
            }
        }

        private Task HandleCommandAsync(ParseResult args)
        {
            throw new NotImplementedException();
        }
    }
}