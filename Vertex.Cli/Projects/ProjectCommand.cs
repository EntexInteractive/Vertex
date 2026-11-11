// Copyright 2026 Entex Interactive

using Vertex.Cli.Net;
using Vertex.Core.Data;
using Vertex.Core.IO;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Projects
{
    public class ProjectCommand : BaseNetCommand
    {
        private readonly Argument<string> _nameArg = new("name") { Description = "Name of the branch to create." };

        private readonly Command _createCmd = new Command("create", "Creates a new branch.");

        public ProjectCommand() : base("project", "Manages projects.")
        {
            _createCmd.SetAction(HandleCreateCommandAsync);
            _createCmd.Add(_nameArg);
            this.Add(_createCmd);

            this.SetAction(HandleCommandAsync);
        }

        private async Task HandleCreateCommandAsync(ParseResult args)
        {
            string serverAddress = args.GetValue(ServerOption) ?? "https://localhost:5000";
            string projectName = args.GetRequiredValue(_nameArg);
            Console.WriteLine(projectName);
            
            using ProjectInterface projectInterface = new(serverAddress);
            ServerResult<VProject> result = await projectInterface.CreateProjectAsync(projectName);
            if (result.IsSuccessStatusCode)
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