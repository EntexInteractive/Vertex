// Copyright 2026 Entex Interactive

using Vertex.Net.Interfaces;

namespace Vertex.Cli.Net
{
    public class PingCommand : Command
    {
        private readonly Option<string> _urlOption = new("url") { Description = "The server url."};
        
        public PingCommand() : base("ping", "Tests the connection to the server.")
        {
            SetAction(HandleCommandAsync);
            Add(_urlOption);
        }

        private async Task HandleCommandAsync(ParseResult parse)
        {
            string url = parse.GetValue(_urlOption) ?? "http://localhost:5000";
            ServerInterface serverInterface = new(url);
            string? version = await serverInterface.GetVersionAsync();
            Console.WriteLine(version);
        }
    }
}