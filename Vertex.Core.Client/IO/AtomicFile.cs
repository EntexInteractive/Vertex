// Copyright 2026 Entex Interactive

using System.Text;

namespace Vertex.Core.Client.IO
{
    /// <summary>
    /// Writes small files so readers see either the old or the new content, never a half-written file.
    /// </summary>
    internal static class AtomicFile
    {
        /// <summary>
        /// Writes <paramref name="text"/> as UTF-8 without a BOM to a temporary file next to <paramref name="path"/>,
        /// then moves it over the target. Creates the folder when missing.
        /// </summary>
        /// <param name="ownerOnly">On Linux and macOS, makes the folder and file readable only by their owner.</param>
        public static void WriteAllText(string path, string text, bool ownerOnly)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            if (ownerOnly && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string temporary = path + ".tmp";
            FileStreamOptions options = new() { Mode = FileMode.Create, Access = FileAccess.Write };
            if (ownerOnly && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (StreamWriter writer = new(temporary, new UTF8Encoding(false), options))
            {
                writer.Write(text);
            }

            File.Move(temporary, path, true);
        }
    }
}
