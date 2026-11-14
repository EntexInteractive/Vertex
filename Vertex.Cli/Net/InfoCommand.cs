// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Spectre.Console;
using Vertex.Core.Data;
using Vertex.Core.Net;
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
            using ServerInterface server = new(settings.ServerAddress, settings.Token);
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
            AnsiConsole.WriteLine($"Logged in as:      {await GetLoginAsync(settings, cancellationToken)}");
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

        /// <summary>
        /// Asks the server who the saved token belongs to, so a revoked or expired login shows as logged out.
        /// </summary>
        private static async Task<string> GetLoginAsync(NetworkSettings settings, CancellationToken cancellationToken)
        {
            if (settings.Token is null) return $"(not logged in; run '{NetworkSettings.LoginCommandFor(settings.ServerAddress)}')";

            try
            {
                using AuthInterface auth = new(settings.ServerAddress, settings.Token);
                ServerResult<VUser> me = await auth.GetCurrentUserAsync(cancellationToken);
                if (!me.IsSuccessStatusCode) return $"(unknown: {ServerErrors.Message(me)})";
                return me.Data!.IsAdmin ? $"{me.Data.Username} (server admin)" : me.Data.Username;
            }
            catch (NotLoggedInException)
            {
                return $"(login expired; run '{NetworkSettings.LoginCommandFor(settings.ServerAddress)}')";
            }
        }
    }
}