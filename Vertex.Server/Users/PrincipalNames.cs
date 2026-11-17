// Copyright 2026 Entex Interactive

namespace Vertex.Server.Users
{
    /// <summary>
    /// Rules for user and group names, which share one namespace.
    /// </summary>
    public static class PrincipalNames
    {
        public const int MaximumLength = 64;

        /// <returns>Why the name can't be used, or null when it's acceptable.</returns>
        public static string? Validate(string? name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return "A name is required.";
            if (name.Length > MaximumLength) return $"Names can be at most {MaximumLength} characters.";

            // Letters, digits, '.', '-' and '_' keep names easy to type in commands and safe in messages.
            if (!name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
                return $"'{name}' can only contain letters, digits, '.', '-' and '_'.";

            return null;
        }
    }
}
