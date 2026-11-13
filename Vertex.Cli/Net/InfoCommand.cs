// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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
            sw.Stop();
            
            if (!result.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Exception?.Message ?? "Unknown error")}[/]");
                return 0;
            }

            InfoResponse info = result.Data!;
            string? clientBuildTimeUtc = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "BuildTimeUtc")?.Value;
            DateTime? clientBuild = DateTime.TryParse(clientBuildTimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsedClient) ? parsedClient : null;
            DateTime? serverBuild = DateTime.TryParse(info.BuildTimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsedServer) ? parsedServer : null;
            
            AnsiConsole.WriteLine($"Current directory: {Directory.GetCurrentDirectory()}");
            AnsiConsole.WriteLine($"Client address:    {info.Address}");
            AnsiConsole.WriteLine($"Client latency:    {sw.ElapsedMilliseconds}ms");
            AnsiConsole.WriteLine($"Client version:    {typeof(Program).Assembly.GetName().Version?.ToString() ?? ""} {(clientBuild?.ToLocalTime().ToString("(MM/dd/yyyy)") ?? "")}");

            AnsiConsole.WriteLine($"Server address:    {settings.ServerAddress}");
            AnsiConsole.WriteLine($"Server date:       {info.Date.ToLocalTime():MM/dd/yyyy HH:mm:ss zzz}");
            AnsiConsole.WriteLine($"Server uptime:     {info.Uptime:g}");
            AnsiConsole.WriteLine($"Server response:   {info.ProcessMs}ms");
            AnsiConsole.WriteLine($"Server version:    {info.Version} {(serverBuild?.ToLocalTime().ToString("(MM/dd/yyyy)") ?? "")}");
            return 0;
        }
    }
}