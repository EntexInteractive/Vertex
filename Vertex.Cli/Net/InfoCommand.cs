// Copyright 2026 Entex Interactive

using System.Diagnostics;
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
            Stopwatch sw = Stopwatch.StartNew();
            using ServerInterface server = new(settings.ServerAddress);
            ServerResult<InfoResponse> result = await server.GetInfoAsync(cancellationToken);
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                return 0;
            }

            InfoResponse info = result.Data!;
            AnsiConsole.WriteLine($"Current directory: {Directory.GetCurrentDirectory()}");
            AnsiConsole.WriteLine($"Client address:    {info.Address}");
            AnsiConsole.WriteLine($"Client latency:    {sw.ElapsedMilliseconds}ms");
            AnsiConsole.WriteLine($"Client version:    {typeof(Program).Assembly.GetName().Version?.ToString() ?? ""}");

            AnsiConsole.WriteLine($"Server address:    {settings.ServerAddress}");
            AnsiConsole.WriteLine($"Server date:       {info.Date.ToLocalTime():MM/dd/yyyy HH:mm:ss zzz}");
            AnsiConsole.WriteLine($"Server uptime:     {info.Uptime:g}");
            AnsiConsole.WriteLine($"Server response:   {info.ProcessMs}ms");
            AnsiConsole.WriteLine($"Server version:    {info.Version} ({info.BuildDate})");
            return 0;
        }
    }
}