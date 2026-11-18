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
using Vertex.Core.Client.Auth;
using Vertex.Core.Client.Workspaces;

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

                // While the session is open, files are locked as they are saved and others' locks show up within seconds.
                WorkspaceMonitor? monitor = StartMonitor();
                Guid? monitoredBranch = CurrentBranchId();
                AnsiConsole.WriteLine();

                while (true)
                {
                    AnsiConsole.Markup(Prompt);

                    string? input = Console.ReadLine();

                    if (input is null || input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
                    if (string.IsNullOrWhiteSpace(input)) continue;
                    string[] commandArgs = SplitArguments(input);

                    // Commands scan and lock on their own; the monitor waits so there's one writer at a time.
                    if (monitor is not null) await monitor.PauseAsync();
                    lock (ConsoleLock) _commandRunning = true;
                    try
                    {
                        await app.RunAsync(commandArgs);
                    }
                    catch (OperationCanceledException)
                    {
                        AnsiConsole.MarkupLine("\n[yellow]Command cancelled.[/]");
                    }
                    finally
                    {
                        // 'vx checkout' moves the workspace to another branch: watch that one from now on.
                        if (CurrentBranchId() != monitoredBranch)
                        {
                            if (monitor is not null) await monitor.DisposeAsync();
                            monitor = StartMonitor();
                            monitoredBranch = CurrentBranchId();
                        }
                        else
                        {
                            monitor?.Resume();
                        }

                        FlushDeferred();
                    }
                }

                if (monitor is not null) await monitor.DisposeAsync();
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

        private const string Prompt = "[purple]Vertex[/] [grey]>[/] ";

        private static readonly object ConsoleLock = new();

        /// <summary>Background messages that arrived while a command was printing, shown once it's done.</summary>
        private static readonly List<string> Deferred = new();

        private static bool _commandRunning;

        /// <summary>A lock change listing more files than this is summarized as a count and folder.</summary>
        private const int ListedActivityPaths = 3;

        /// <summary>
        /// The branch the workspace the session was opened in follows, read fresh from its config.
        /// </summary>
        private static Guid? CurrentBranchId()
        {
            return VWorkspace.Find(Directory.GetCurrentDirectory())?.Config.BranchId;
        }

        /// <summary>
        /// Starts watching the workspace the session was opened in, when it's linked to a project and logged in.
        /// </summary>
        private static WorkspaceMonitor? StartMonitor()
        {
            VWorkspace? workspace = VWorkspace.Find(Directory.GetCurrentDirectory());
            if (workspace is null || LinkedWorkspace.TryFrom(workspace) is not LinkedWorkspace linked) return null;

            NetworkSettings network = new();
            if (network.Credential is null)
            {
                AnsiConsole.MarkupLine($"[grey]Not logged in to {Markup.Escape(network.ServerAddress)}, so files aren't locked as you save them. Run '{Markup.Escape(NetworkSettings.LoginCommandFor(network.ServerAddress))}' and reopen vx.[/]");
                return null;
            }

            string server = network.ServerAddress;
            WorkspaceMonitor monitor = new(workspace, server, () => CredentialStore.Get(server)?.Token, linked.ProjectId, linked.BranchId, linked.WorkspaceId, network.Credential.Username);
            monitor.LockConflict += held =>
            {
                string path = SyncCommand.CommandPath(workspace, held.Path);
                Notify($"[red]! {Markup.Escape(path)} is {Markup.Escape(LockOutput.Describe(workspace, held))}.[/]");
            };

            monitor.Activity += item =>
            {
                foreach (string line in DescribeActivity(workspace, item)) Notify($"[grey]{Markup.Escape(line)}[/]");
            };

            monitor.ProblemChanged += problem =>
            {
                if (problem is null)
                {
                    Notify("[grey]Connected again; file locks are up to date.[/]");
                    return;
                }

                // Outside a command there are no current settings, so name this workspace's server explicitly.
                string reason = ServerErrors.Describe(problem, server) ?? (problem is OperationCanceledException ? "the server didn't answer in time." : problem.Message);
                Notify($"[yellow]Couldn't update file locks: {Markup.Escape(reason)} Retrying in the background.[/]");
            };

            monitor.Start();
            return monitor;
        }

        /// <summary>
        /// Prints a background message on its own line and puts the prompt back. While a command runs, the message
        /// waits until it's done so it never lands in the middle of the command's output.
        /// </summary>
        private static void Notify(string markup)
        {
            lock (ConsoleLock)
            {
                if (_commandRunning)
                {
                    Deferred.Add(markup);
                    return;
                }

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(markup);
                AnsiConsole.Markup(Prompt);
            }
        }

        private static void FlushDeferred()
        {
            lock (ConsoleLock)
            {
                _commandRunning = false;
                foreach (string markup in Deferred) AnsiConsole.MarkupLine(markup);
                Deferred.Clear();
            }
        }

        /// <summary>
        /// What someone else did, in the CLI's voice: "alice locked Content/Hero.uasset", "alice locked 2,341 files in
        /// Content/Maps", "alice submitted #42 "Fix jump" (3 files). Run 'vx sync' to get it." Paths are relative to
        /// the current folder, as every command prints them.
        /// </summary>
        private static IEnumerable<string> DescribeActivity(VWorkspace workspace, ActivityItem item)
        {
            string who = item.SameUser ? "you" : item.Username;
            string where = item.SameUser && item.Host.Length > 0 ? $" on {item.Host}" : string.Empty;
            if (item.Kind == ActivityKind.Submitted)
            {
                string message = (item.Message ?? string.Empty).Split('\n', 2)[0].Trim();
                if (message.Length > 60) message = message[..57] + "...";
                yield return $"{who} submitted #{item.Revision} \"{message}\" ({item.Files:N0} file(s)){where}. Run 'vx sync' to get it.";
                yield break;
            }

            string verb = item.Kind == ActivityKind.Locked ? "locked" : "unlocked";
            if (item.Paths.Count <= ListedActivityPaths)
            {
                foreach (string path in item.Paths) yield return $"{who} {verb} {SyncCommand.CommandPath(workspace, path)}{where}";
                yield break;
            }

            string folder = CommonFolder(item.Paths);
            string place = folder.Length == 0 ? "across the project" : $"in {SyncCommand.CommandPath(workspace, folder)}";
            yield return $"{who} {verb} {item.Paths.Count:N0} files {place}{where}";
        }

        /// <summary>
        /// The deepest folder containing every path, or "" when they only share the workspace root.
        /// </summary>
        private static string CommonFolder(IReadOnlyList<string> paths)
        {
            string[] common = paths[0].Split('/')[..^1];
            int length = common.Length;
            foreach (string path in paths.Skip(1))
            {
                string[] segments = path.Split('/');
                int shared = 0;
                while (shared < length && shared < segments.Length - 1 && string.Equals(segments[shared], common[shared], StringComparison.OrdinalIgnoreCase)) shared++;
                length = shared;
                if (length == 0) break;
            }

            return string.Join('/', common[..length]);
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