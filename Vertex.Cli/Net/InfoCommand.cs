// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Newtonsoft.Json.Linq;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Net
{
    public class InfoCommand : BaseNetCommand
    {
        public InfoCommand() : base("info", "Display connection information.")
        {
            SetAction(HandleCommandAsync);
            Add(ServerOption);
        }

        private async Task HandleCommandAsync(ParseResult parse)
        {
            string serverAddress = parse.GetValue(ServerOption) ?? "https://localhost:5000";
            using ServerInterface server = new(serverAddress);

            ServerResult<InfoResponse> version = await server.GetInfoAsync();
            if (!version.IsSuccessStatusCode)
            {
                Terminal.Error(version.Exception?.Message ?? "Unknown error");
                return;
            }

            InfoResponse info = version.Data!;
            string[] data =
            [
                $"Current directory: {Directory.GetCurrentDirectory()}",
                $"Client version:    {typeof(Program).Assembly.GetName().Version}",
                $"Server address:    {info.Address}",
                $"Server date:       {info.Date.ToLocalTime():yyyy/MM/dd HH:mm:ss zzz}",
                $"Server uptime:     {info.Uptime}",
                $"Server response:   {info.ProcessMs}ms",
                $"Server version:    {info.Version}",
            ];

            Terminal.WriteArray("Vertex information", data);
        }
    }
}