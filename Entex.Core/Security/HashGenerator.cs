using System.Security.Cryptography;
using System.Text;

namespace Entex.Shared.Security
{
    /// <summary>
    /// Provides functionality for generating random hashes. This class cannot be inherited.
    /// </summary>
    public static class HashGenerator
    {
        /// <summary>
        /// Generates a random hash.
        /// </summary>
        /// <param name="length"></param>
        /// <param name="lowercase"></param>
        /// <returns></returns>
        public static string GenerateHash(int length, bool lowercase = false)
        {
            return RandomNumberGenerator.GetHexString(length, lowercase);
        }

        /// <summary>
        /// Generates a random password.
        /// </summary>
        /// <param name="length"></param>
        /// <param name="symbols"></param>
        /// <returns></returns>
        public static string GeneratePassword(int length, bool symbols = true)
        {
            char[] chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890".ToCharArray();
            char[] syms = "@#$%^&*()_+-={}[]:;,.?".ToCharArray();

            // Joins the symbols if applicable
            if (symbols) chars = chars.Concat(syms).ToArray();

            StringBuilder password = new StringBuilder();
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] data = new byte[4 * length];
                rng.GetBytes(data);

                for (int i = 0; i < length; i++)
                {
                    uint rand = BitConverter.ToUInt32(data, i * 4);
                    long index = rand % chars.Length;
                    password.Append(chars[index]);
                }
            }

            return password.ToString();
        }

        /// <summary>
        /// Computes a SHA1 hash from a string.
        /// </summary>
        /// <param name="value">The string to hash.</param>
        /// <returns>A <see cref="SHA1"/> hash as a string.</returns>
        [Obsolete("SHA1 is outdated and vulnerable. Please use SHA256 instead.")]
        public static string CreateSHA1(string value)
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            return Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(value))).ToLower();
        }

        /// <summary>
        /// Computes a SHA256 hash from a string.
        /// </summary>
        /// <param name="value">The string to hash.</param>
        /// <returns>A <see cref="SHA256"/> hash as a string.</returns>
        public static string CreateSHA256(string value)
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLower();
        }
    }
}
