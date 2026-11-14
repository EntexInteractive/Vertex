// Copyright 2026 Entex Interactive

using System.Reflection;
using System.Text.RegularExpressions;
using EntexInteractive.Extensions;
using Serilog.Events;
using Spectre.Console;
using Spectre.Console.Cli;
using Vertex.Cli.Files;
using Vertex.Cli.Net;
using Vertex.Cli.Projects;
using Vertex.Cli.Branches;

namespace Vertex.Cli
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            AssemblyName assembly = Assembly.GetExecutingAssembly().GetName();

            // Bootstrap debug detection before configuring logging.
            Terminal.IsDebugging = args.Contains("-log", StringComparer.OrdinalIgnoreCase);
            LoggerConfiguration loggerConfig = new LoggerConfiguration().MinimumLevel.Is(Terminal.IsDebugging ? LogEventLevel.Debug : LogEventLevel.Information);
            if (Terminal.IsDebugging) loggerConfig.WriteTo.Console();

            Log.Logger = loggerConfig.CreateLogger();
            Log.Information("{AssemblyName} [Version {AssemblyVersion}]", assembly.Name, assembly.Version);
            Log.Debug("Running in: {CurrentDirectory}", Directory.GetCurrentDirectory());

            CommandApp app = new CommandApp();
            app.Configure(config =>
            {
                config.SetApplicationName("vx");
                config.SetInterceptor(new CurrentSettingsInterceptor());
                config.SetExceptionHandler((ex, resolver) =>
                {
                    // Errors any command can hit, such as not being logged in, get one line naming the fix.
                    if (ServerErrors.Describe(ex) is string known)
                    {
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(known)}[/]");
                        Log.Debug(ex, "Command execution failed");
                        return;
                    }

                    // Some exceptions (e.g. a missing assembly in a single-file build) have an empty message; never
                    // fail silently.
                    string message = string.IsNullOrWhiteSpace(ex?.Message) ? ex?.GetType().Name ?? "Unknown error" : ex.Message;
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
                    if (!Terminal.IsDebugging) AnsiConsole.MarkupLine("[grey]Run the command again with -log for details.[/]");
                    Log.Error(ex, "Command execution failed");
                });

                Startup.RegisterCommands(config);
            });

            try
            {
                // If args are provided, run the command.
                if (args.Length > 0) return await app.RunAsync(args);

                // Run as if full application.
                AnsiConsole.Write(new FigletText("Vertex").Color(Color.Purple));
                AnsiConsole.MarkupLine("[grey]Run '--help' to see available commands.[/]");
                AnsiConsole.WriteLine();

                while (true)
                {
                    AnsiConsole.Markup("[purple]Vertex[/] [grey]>[/] ");

                    string? input = Console.ReadLine();

                    if (input is null || input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
                    if (string.IsNullOrWhiteSpace(input)) continue;
                    string[] commandArgs = SplitArguments(input);

                    try
                    {
                        await app.RunAsync(commandArgs);
                    }
                    catch (OperationCanceledException)
                    {
                        AnsiConsole.MarkupLine("\n[yellow]Command cancelled.[/]");
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                Terminal.Error(ex);
                throw;
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        }

        /// <summary>
        /// Remembers the running command's settings, so error messages can name the server it talked to.
        /// </summary>
        private sealed class CurrentSettingsInterceptor : ICommandInterceptor
        {
            public void Intercept(CommandContext context, CommandSettings settings)
            {
                NetworkSettings.Current = settings as NetworkSettings;
            }
        }

        private static string[] SplitArguments(string input)
        {
            return Regex.Matches(input, @"[\""].+?[\""]|[^ ]+").Cast<Match>().Select(m => m.Value.Replace("\"", "")).ToArray();
        }
    }
}