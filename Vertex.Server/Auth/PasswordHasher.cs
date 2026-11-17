// Copyright 2026 Entex Interactive

using System.Security.Cryptography;

namespace Vertex.Server.Auth
{
    /// <summary>
    /// Hashes passwords with PBKDF2-SHA256 and a random salt per password, stored as
    /// <c>pbkdf2-sha256$iterations$salt$hash</c> (base64), so the iteration count can be raised later without
    /// breaking existing hashes.
    /// </summary>
    public static class PasswordHasher
    {
        /// <summary>The shortest password the server accepts.</summary>
        public const int MinimumLength = 8;

        private const string Scheme = "pbkdf2-sha256";
        private const int Iterations = 600_000; // OWASP's 2023 recommendation for PBKDF2-SHA256.
        private const int SaltSize = 16;
        private const int HashSize = 32;

        // Verified against when the user doesn't exist, so a failed login takes as long either way and doesn't reveal
        // which usernames exist.
        private static readonly string DummyHash = Hash("vertex-dummy-password");

        public static string Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <param name="password">The password the user typed.</param>
        /// <param name="stored">The stored hash, or null when the user doesn't exist or has no password.</param>
        public static bool Verify(string password, string? stored)
        {
            if (stored is null)
            {
                Verify(password, DummyHash);
                return false;
            }

            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out int iterations)) return false;

            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        /// <returns>Why the password can't be used, or null when it's acceptable.</returns>
        public static string? Validate(string? password)
        {
            return string.IsNullOrEmpty(password) || password.Length < MinimumLength
                ? $"Passwords must be at least {MinimumLength} characters."
                : null;
        }
    }
}
