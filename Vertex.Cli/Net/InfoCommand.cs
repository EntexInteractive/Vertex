// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Spectre.Console;
using Vertex.Core.Client.Workspaces;
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
            InfoResponse? info = null;
            string? serverError = null;

            try
            {
                using ServerInterface server = new(settings.ServerAddress, settings.Token);
                ServerResult<InfoResponse> result = await server.GetInfoAsync(cancellationToken);

                if (result.IsSuccessStatusCode) info = result.Data;
                else serverError = result.Exception?.Message ?? "Server unavailable";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                serverError = ex.Message;
            }
            catch (TaskCanceledException)
            {
                serverError = "Connection timed out";
            }
            finally
            {
                sw.Stop();
            }

            string? clientBuildTimeUtc = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "BuildTimeUtc")?.Value;
            DateTime? clientBuild = DateTime.TryParse(clientBuildTimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsedClient) ? parsedClient : null;
            DateTime? serverBuild = DateTime.TryParse(info?.BuildTimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsedServer) ? parsedServer : null;
            VWorkspace? workspace = VWorkspace.Find(Directory.GetCurrentDirectory());

            // Local information (always available)
            AnsiConsole.WriteLine($"Logged in as:      {(info != null ? await GetLoginAsync(settings, cancellationToken) : "(server unavailable)")}");
            AnsiConsole.WriteLine($"Current directory: {Directory.GetCurrentDirectory()}");
            AnsiConsole.WriteLine($"Current Project:   {(string.IsNullOrWhiteSpace(workspace?.Config.Project) ? "(no project found; run vx init to create one)" : workspace.Config.Root)}");
            AnsiConsole.WriteLine($"Client address:    {info?.Address ?? "Unavailable"}");
            AnsiConsole.WriteLine($"Client latency:    {(info != null ? $"{sw.ElapsedMilliseconds}ms" : "Unavailable")}");
            AnsiConsole.WriteLine($"Client version:    {typeof(Program).Assembly.GetName().Version?.ToString() ?? ""} {(clientBuild?.ToLocalTime().ToString("(MM/dd/yyyy)") ?? "")}");

            // Server information
            AnsiConsole.WriteLine($"Server address:    {settings.ServerAddress}");

            if (info != null)
            {
                AnsiConsole.WriteLine($"Server date:       {info.Date.ToLocalTime():MM/dd/yyyy HH:mm:ss zzz}");
                AnsiConsole.WriteLine($"Server uptime:     {info.Uptime:g}");
                AnsiConsole.WriteLine($"Server response:   {info.ProcessMs}ms");
                AnsiConsole.WriteLine($"Server version:    {info.Version} {(serverBuild?.ToLocalTime().ToString("(MM/dd/yyyy)") ?? "")}");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Server status:     Offline[/]");
            }

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