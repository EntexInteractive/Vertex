using System.Text.RegularExpressions;

namespace Entex.Shared.Text
{
    /// <summary>
    /// Represents command-line parsing and formatting. This class cannot be inherited.
    /// </summary>
    public static class CommandParser
    {
        /// <summary>
        /// Finds a argument in a command.
        /// </summary>
        /// <param name="args">A array of parsed arguments.</param>
        /// <param name="keys">A array of keys to find.</param>
        /// <returns>true if found, otherwise false.</returns>
        public static bool TryArgument(IEnumerable<string> args, params string[] keys)
        {
            int i = keys.Select(s => Array.IndexOf(args.ToArray(), s)).FirstOrDefault();
            return i >= 0;
        }

        /// <summary>
        /// Finds a argument in a command line.
        /// </summary>
        /// <param name="args">A array of parsed arguments.</param>
        /// <param name="keys">A array of keys to find.</param>
        /// <returns>The found parameter value.</returns>
        public static T? FindArgument<T>(IEnumerable<string> args, params string[] keys)
        {
            int i = keys.Select(s => Array.IndexOf(args.ToArray(), s)).FirstOrDefault();
            if (i == -1) return default(T);
            return (T)System.Convert.ChangeType(args.ToArray()[i + 1], typeof(T));
        }

        /// <summary>
        /// Parses a command using the <see cref="Regex"/> expression class.
        /// </summary>
        /// <param name="line">The command to be parsed.</param>
        /// <returns>A array of command arguments.</returns>
        public static string[] Parse(string line)
        {
            return Regex.Matches(line, @"[\""].+?[\""]|[^ ]+").Cast<Match>().Select(m => m.Value.Replace("\"", "")).ToArray();
        }

        /// <summary>
        /// Parses a command using the <see cref="Regex"/> expression class.
        /// </summary>
        /// <param name="line">The command to be parsed.</param>
        /// <param name="results">The out results of a successful parse.</param>
        /// <returns>true if arguments were found, otherwise false.</returns>
        public static bool TryParse(string line, out string[] results)
        {
            results = Parse(line);
            return results.Length > 0;
        }
    }
}
