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
                $"Server address:    {info.Address}",
                $"Server version:    {info.Version}",
                $"Server date:       {info.Date.ToLocalTime():yyyy/MM/dd HH:mm:ss zzz}",
                $"Server response:   {info.ProcessMs}ms",
                $"Server uptime:     {info.Uptime}"
            ];

            Terminal.WriteArray("Vertex information", data.OrderBy(x => x));
        }
    }
}