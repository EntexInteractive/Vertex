// Copyright 2026 Entex Interactive

using Vertex.Net.Interfaces;

namespace Vertex.Cli.Net
{
    public class PingCommand : BaseNetCommand
    {
        public PingCommand() : base("ping", "Tests the connection to the server.")
        {
            SetAction(HandleCommandAsync);
        }

        private async Task HandleCommandAsync(ParseResult parse)
        {
            string url = parse.GetValue(ServerOption) ?? "http://localhost:5000";
            ServerInterface serverInterface = new(url);
            string? version = await serverInterface.GetVersionAsync();
            Console.WriteLine(version);
        }
    }
}