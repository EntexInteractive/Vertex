// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Newtonsoft.Json.Linq;
using Vertex.Net.Interfaces;
using Vertex.Net.Responses;
using Vertex.Net.Results;

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
            ServerInterface server = new(parse.GetValue(ServerOption) ?? "http://localhost:5000");

            ServerResult<InfoResponse> version = await server.GetInfoAsync();
            if (!version.IsSuccess)
            {
                Terminal.Error(version.Exception);
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