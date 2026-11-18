// Copyright 2026 Entex Interactive

using Vertex.Mirror.Pull;
using Vertex.Mirror.Push;

namespace Vertex.Mirror
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            using CancellationTokenSource cancel = new();
            Console.CancelKeyPress += (_, e) =>
            {
                // First Ctrl+C stops after the current step and prints the summary; a second one exits immediately.
                if (cancel.IsCancellationRequested) return;
                e.Cancel = true;
                AnsiConsole.MarkupLine("[yellow]Stopping... (Ctrl+C again to exit right away)[/]");
                cancel.Cancel();
            };

            CommandApp app = new();
            app.Configure(config =>
            {
                config.SetApplicationName("vx-mirror");
                config.AddExample("pull", "--project", "gitignore", "--git", "https://github.com/github/gitignore.git");
                config.AddExample("pull", "--project", "vertex-self", "--git", @"D:\Git\Vertex", "--start", "1", "--interval", "1m");
                config.AddExample("push", "--project", "game", "--git", "git@github.com:studio/game-mirror.git");
                config.AddExample("push", "--project", "game", "--p4", "game-mirror-ws", "--once");

                config.AddCommand<PullCommand>("pull")
                    .WithDescription("git -> Vertex: replays a git repository's commits into a Vertex branch, one per --interval, and checks every byte comes back the same.");
                config.AddCommand<PushCommand>("push")
                    .WithDescription("Vertex -> git or Perforce: commits every new revision of a Vertex branch to a git repository (--git) or a Perforce workspace (--p4).");
            });

            return await app.RunAsync(args, cancel.Token);
        }
    }
}
