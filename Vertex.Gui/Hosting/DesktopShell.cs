// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Photino.NET;

namespace Vertex.Gui.Hosting
{
    /// <summary>
    /// What the app does through the operating system: the native folder picker and showing files in Explorer,
    /// Finder or the Linux file manager.
    /// </summary>
    public sealed class DesktopShell
    {
        private PhotinoWindow? _window;

        /// <summary>
        /// Set once the window exists. Until then, and in tests, the folder picker is unavailable.
        /// </summary>
        public void Attach(PhotinoWindow window)
        {
            _window = window;
        }

        /// <summary>
        /// Shows the native folder picker and returns the chosen folder, or null when it was closed.
        /// </summary>
        /// <exception cref="HostProblemException">There is no window to show it on.</exception>
        public string? PickFolder(string title, string? startIn)
        {
            PhotinoWindow window = _window ?? throw new HostProblemException("The folder picker needs the desktop window. Type the folder's path instead.", StatusCodes.Status409Conflict);
            string[]? chosen = null;
            string? start = startIn is not null && Directory.Exists(startIn) ? startIn : null;
            window.Invoke(() => chosen = window.ShowOpenFolder(title, start, false));
            return chosen is { Length: > 0 } && !string.IsNullOrWhiteSpace(chosen[0]) ? chosen[0] : null;
        }

        /// <summary>
        /// Opens the file manager with <paramref name="fullPath"/> selected, or opens the folder when it's one.
        /// </summary>
        public void Reveal(string fullPath)
        {
            bool isFile = File.Exists(fullPath);
            string target = isFile ? fullPath : Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath;
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(isFile ? $"/select,{target}" : target);
            }
            else if (OperatingSystem.IsMacOS())
            {
                start = new ProcessStartInfo("open") { UseShellExecute = false };
                if (isFile) start.ArgumentList.Add("-R");
                start.ArgumentList.Add(target);
            }
            else
            {
                start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(isFile ? Path.GetDirectoryName(target) ?? target : target);
            }

            using Process? _ = Process.Start(start);
        }
    }
}
