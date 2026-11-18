// Copyright 2026 Entex Interactive

using System.ComponentModel;
using System.Diagnostics;

namespace Vertex.Mirror
{
    /// <summary>
    /// A command-line tool the mirror drives (git, p4), run without a shell so arguments never need quoting.
    /// </summary>
    public sealed class ExternalTool
    {
        private readonly string _executable;
        private readonly string _installHint;
        private readonly IReadOnlyList<string> _globalArguments;
        private readonly IReadOnlyDictionary<string, string> _environment;

        public ExternalTool(string executable, string installHint, IReadOnlyList<string>? globalArguments = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            _executable = executable;
            _installHint = installHint;
            _globalArguments = globalArguments ?? [];
            _environment = environment ?? new Dictionary<string, string>();
        }

        /// <summary>
        /// Runs the tool and returns its trimmed output.
        /// </summary>
        /// <exception cref="ToolFailedException">It exited with an error (the message is the tool's).</exception>
        /// <exception cref="MirrorSetupException">It isn't installed.</exception>
        public Task<string> RunAsync(string? workingDirectory, CancellationToken ct, params string[] args)
        {
            return RunAsync(workingDirectory, null, ct, args);
        }

        /// <summary>
        /// Runs the tool with extra environment variables (e.g. a git commit's author) and returns its trimmed output.
        /// </summary>
        public async Task<string> RunAsync(string? workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct, params string[] args)
        {
            ProcessStartInfo info = new(_executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };
            foreach (string arg in _globalArguments) info.ArgumentList.Add(arg);
            foreach (string arg in args) info.ArgumentList.Add(arg);
            foreach ((string name, string value) in _environment) info.Environment[name] = value;
            if (environment is not null)
                foreach ((string name, string value) in environment)
                    info.Environment[name] = value;

            Process process;
            try
            {
                process = Process.Start(info) ?? throw new ToolFailedException($"{_executable} didn't start.");
            }
            catch (Win32Exception)
            {
                throw new MirrorSetupException($"vx-mirror needs {_executable}. {_installHint}");
            }

            using (process)
            {
                // Nothing reads stdin: close it so a tool that wants to prompt fails instead of hanging.
                process.StandardInput.Close();
                Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
                Task<string> error = process.StandardError.ReadToEndAsync(ct);
                try
                {
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already exited.
                    }

                    throw;
                }

                string text = await output;
                string message = (await error).Trim();
                if (process.ExitCode != 0)
                {
                    // The subcommand ("clone", "submit"): the first plain word, not an option or an option's value.
                    string command = args.FirstOrDefault(a => a.Length > 0 && a.All(c => char.IsAsciiLetterLower(c) || c == '-') && a[0] != '-') ?? string.Empty;
                    throw new ToolFailedException($"{_executable} {command} failed: {(message.Length > 0 ? message : text.Trim())}", message.Length > 0 ? message : text);
                }

                return text.Trim();
            }
        }
    }

    /// <summary>
    /// A tool exited with an error. <see cref="Output"/> is what it printed about it.
    /// </summary>
    public sealed class ToolFailedException(string message, string output = "") : Exception(message)
    {
        public string Output { get; } = output;
    }
}
