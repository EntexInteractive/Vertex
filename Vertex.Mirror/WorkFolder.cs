// Copyright 2026 Entex Interactive

namespace Vertex.Mirror
{
    /// <summary>
    /// The folder a run keeps its working copies in: --work when given (kept), otherwise a new folder in the system
    /// temp folder that is deleted at the end.
    /// </summary>
    public sealed class WorkFolder : IDisposable
    {
        private readonly bool _temporary;

        private WorkFolder(string path, bool temporary)
        {
            Path = path;
            _temporary = temporary;
        }

        public string Path { get; }

        public static WorkFolder Create(string? given, string runId)
        {
            bool temporary = given is null;
            string path = System.IO.Path.GetFullPath(given ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vx-mirror", runId));
            Directory.CreateDirectory(path);
            return new WorkFolder(path, temporary);
        }

        /// <summary>
        /// A sub-folder, created when missing.
        /// </summary>
        public string Sub(string name)
        {
            string path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// Deletes a folder tree, clearing read-only flags first (git objects and Perforce files are read-only).
        /// </summary>
        public static void DeleteTree(string path)
        {
            if (!Directory.Exists(path)) return;
            foreach (string file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }

        public void Dispose()
        {
            if (!_temporary) return;
            try
            {
                DeleteTree(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[yellow]Could not delete {Markup.Escape(Path)}: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }
}
