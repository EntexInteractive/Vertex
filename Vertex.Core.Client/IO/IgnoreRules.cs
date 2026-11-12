// Copyright 2026 Entex Interactive

using System.Text;
using System.Text.RegularExpressions;

namespace Vertex.Core.Client.IO
{
    /// <summary>
    /// Patterns from a workspace's <c>.vxignore</c>, using .gitignore syntax: <c>*</c>, <c>?</c>, <c>[abc]</c>,
    /// <c>**</c>, a trailing <c>/</c> for directories only, a leading or inner <c>/</c> to anchor to the workspace root,
    /// <c>!</c> to re-include, and <c>#</c> for comments. The last matching pattern wins.
    /// </summary>
    /// <remarks>
    /// Ignore rules only apply to untracked files. A file that is already tracked stays tracked when a pattern
    /// starts matching it, the same as git, so editing <c>.vxignore</c> never deletes anything from the server.
    /// </remarks>
    public sealed class IgnoreRules
    {
        public const string FileName = ".vxignore";

        private static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        private static readonly StringComparison NameComparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private readonly Rule[] _rules;

        public static IgnoreRules Empty { get; } = new([]);

        public int Count => _rules.Length;

        private IgnoreRules(Rule[] rules)
        {
            _rules = rules;
        }

        /// <summary>
        /// Reads <c>.vxignore</c> from the workspace root. Returns <see cref="Empty"/> when there is none.
        /// </summary>
        public static IgnoreRules Load(string root)
        {
            string path = Path.Combine(root, FileName);
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : Empty;
        }

        public static IgnoreRules Parse(IEnumerable<string> lines)
        {
            List<Rule> rules = new();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                bool negate = line[0] == '!';
                if (negate) line = line[1..];

                bool directoryOnly = line.EndsWith('/');
                line = line.TrimEnd('/');

                // A slash anywhere but the end anchors the pattern to the workspace root; otherwise it matches a
                // name at any depth.
                bool anchored = line.Contains('/');
                line = line.TrimStart('/');
                if (line.Length == 0) continue;

                rules.Add(Rule.Create(line, anchored, negate, directoryOnly));
            }

            return new IgnoreRules(rules.ToArray());
        }

        /// <summary>
        /// Returns whether a path matches the rules.
        /// </summary>
        /// <param name="relativePath">Path relative to the workspace root, with <c>/</c> separators.</param>
        /// <param name="isDirectory">Whether the path is a directory; directory-only patterns skip files.</param>
        public bool IsIgnored(string relativePath, bool isDirectory)
        {
            if (_rules.Length == 0) return false;

            int slash = relativePath.LastIndexOf('/');
            string name = slash < 0 ? relativePath : relativePath[(slash + 1)..];

            bool ignored = false;
            foreach (Rule rule in _rules)
            {
                if (rule.DirectoryOnly && !isDirectory) continue;
                if (ignored == !rule.Negate) continue; // Matching can't change the outcome.
                if (rule.IsMatch(relativePath, name)) ignored = !rule.Negate;
            }

            return ignored;
        }

        private sealed class Rule
        {
            private enum Kind
            {
                Name,
                Suffix,
                NameGlob,
                PathGlob
            }

            private readonly Kind _kind;
            private readonly string _literal = "";
            private readonly Regex? _regex;

            public bool Negate { get; }
            public bool DirectoryOnly { get; }

            private Rule(Kind kind, string literal, Regex? regex, bool negate, bool directoryOnly)
            {
                _kind = kind;
                _literal = literal;
                _regex = regex;
                Negate = negate;
                DirectoryOnly = directoryOnly;
            }

            public static Rule Create(string pattern, bool anchored, bool negate, bool directoryOnly)
            {
                // Most patterns are plain names ("bin") or extensions ("*.log"); match those without a regex.
                if (!anchored)
                {
                    if (!HasWildcard(pattern)) return new Rule(Kind.Name, Unescape(pattern), null, negate, directoryOnly);
                    if (pattern[0] == '*' && !HasWildcard(pattern[1..])) return new Rule(Kind.Suffix, Unescape(pattern[1..]), null, negate, directoryOnly);
                }

                RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.Compiled | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                Regex regex = new("^" + GlobToRegex(pattern) + "$", options);
                return new Rule(anchored ? Kind.PathGlob : Kind.NameGlob, "", regex, negate, directoryOnly);
            }

            public bool IsMatch(string path, string name)
            {
                return _kind switch
                {
                    Kind.Name => name.Equals(_literal, NameComparison),
                    Kind.Suffix => name.EndsWith(_literal, NameComparison),
                    Kind.NameGlob => _regex!.IsMatch(name),
                    _ => _regex!.IsMatch(path)
                };
            }

            private static bool HasWildcard(string pattern)
            {
                return pattern.AsSpan().IndexOfAny("*?[\\") >= 0;
            }

            private static string Unescape(string pattern)
            {
                return pattern.Replace("\\", "");
            }

            private static string GlobToRegex(string glob)
            {
                StringBuilder sb = new();
                for (int i = 0; i < glob.Length; i++)
                {
                    char c = glob[i];
                    if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        bool atStart = i == 0 || glob[i - 1] == '/';
                        bool slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                        if (atStart && slashAfter)
                        {
                            sb.Append("(?:.*/)?"); // "**/" matches zero or more directories.
                            i += 2;
                        }
                        else
                        {
                            sb.Append(".*");
                            i++;
                        }
                    }
                    else if (c == '*') sb.Append("[^/]*");
                    else if (c == '?') sb.Append("[^/]");
                    else if (c == '\\' && i + 1 < glob.Length) sb.Append(Regex.Escape(glob[++i].ToString()));
                    else if (c == '[' && glob.IndexOf(']', i + 1) is int end and > 0)
                    {
                        string set = glob[(i + 1)..end];
                        if (set.StartsWith('!')) set = "^" + set[1..];
                        sb.Append('[').Append(set.Replace("\\", "\\\\")).Append(']');
                        i = end;
                    }
                    else sb.Append(Regex.Escape(c.ToString()));
                }

                return sb.ToString();
            }
        }
    }
}