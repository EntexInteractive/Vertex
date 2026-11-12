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
                config.SetExceptionHandler((ex, resolver) =>
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex?.Message ?? "Unknown error")}[/]");
                    Log.Error(ex, "Command execution failed");
                });

                Startup.RegisterCommands(config);
            });

            try
            {
                // If args are provided, run the command.
                if (args.Length > 0) return await app.RunAsync(args);

                // Run as if full application.
                AnsiConsole.Write(new FigletText("Vertex").Color(Color.Violet));
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

        private static string[] SplitArguments(string input)
        {
            return Regex.Matches(input, @"[\""].+?[\""]|[^ ]+").Cast<Match>().Select(m => m.Value.Replace("\"", "")).ToArray();
        }
    }
}