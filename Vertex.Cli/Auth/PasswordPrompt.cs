// Copyright 2026 Entex Interactive

namespace Vertex.Cli.Auth
{
    /// <summary>
    /// Reads passwords from the terminal without echoing them, or from standard input for scripts and CI.
    /// </summary>
    public static class PasswordPrompt
    {
        /// <param name="prompt">The prompt shown in the terminal, e.g. "Password:".</param>
        /// <param name="fromStdin">Read the whole of standard input instead of prompting.</param>
        /// <param name="scriptedHint">The command to suggest when there's no terminal to prompt in.</param>
        /// <param name="confirm">Ask twice and check both match, for setting a new password.</param>
        /// <returns>The password, or null after printing why it couldn't be read.</returns>
        public static async Task<string?> ReadAsync(string prompt, bool fromStdin, string scriptedHint, bool confirm, CancellationToken cancellationToken)
        {
            if (fromStdin)
            {
                string piped = (await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n');
                if (piped.Length > 0) return piped;

                AnsiConsole.MarkupLine("[red]No password on standard input.[/]");
                return null;
            }

            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine($"[red]Can't prompt for a password without a terminal. Run '{Markup.Escape(scriptedHint)}'.[/]");
                return null;
            }

            string password = await AnsiConsole.PromptAsync(new TextPrompt<string>(prompt).Secret(), cancellationToken);
            if (!confirm) return password;

            string again = await AnsiConsole.PromptAsync(new TextPrompt<string>("Confirm password:").Secret(), cancellationToken);
            if (password == again) return password;

            AnsiConsole.MarkupLine("[red]The passwords don't match.[/]");
            return null;
        }
    }
}
