// Copyright 2026 Entex Interactive

using Spectre.Console;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Cli.Net
{
    public sealed class InfoCommand : AsyncCommand<NetworkSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, NetworkSettings settings, CancellationToken cancellationToken)
        {
            using ServerInterface server = new(settings.Server ?? "http://localhost:5000");
            ServerResult<InfoResponse> result = await server.GetInfoAsync(cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                return 0;
            }

            InfoResponse info = result.Data!;
            Table table = new Table().Border(TableBorder.Rounded).AddColumn("Property").AddColumn("Value");
            table.AddRow("Current directory", Directory.GetCurrentDirectory());
            table.AddRow("Client version", typeof(Program).Assembly.GetName().Version?.ToString() ?? "");
            table.AddRow("Server address", info.Address ?? "");
            table.AddRow("Server date", info.Date.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz"));
            table.AddRow("Server uptime", info.Uptime.ToString());
            table.AddRow("Server response", $"{info.ProcessMs}ms");
            table.AddRow("Server version", info.Version ?? "");
            AnsiConsole.Write(table);
            return 0;
        }
    }
}