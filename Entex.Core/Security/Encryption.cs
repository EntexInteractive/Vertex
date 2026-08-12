using System.Security.Cryptography;
using System.Text;

namespace Entex.Shared.Security
{
    /// <summary>
    /// Provides functionality for encryption. This class cannot be inherited.
    /// </summary>
    public static class Encryption
    {
        /// <summary>
        /// Converts a string of UTF-8 characters to a base64 string.
        /// </summary>
        /// <param name="value">The string to be encoded.</param>
        /// <returns>A base64 encoded string.</returns>
        public static string Encode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            byte[] data = Encoding.UTF8.GetBytes(value);
            string encoded = Convert.ToBase64String(data);
            return encoded;
        }

        /// <summary>
        /// Converts a base64 string to a string of UTF-8 characters.
        /// </summary>
        /// <param name="value">The base64 string to decode.</param>
        public static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            byte[] data = Convert.FromBase64String(value);
            string decoded = Encoding.UTF8.GetString(data);
            return decoded;
        }

        /// <summary>
        /// Encrypts an array of bytes with a hash.
        /// </summary>
        /// <param name="data">The data to be encoded.</param>
        /// <param name="hash">The hash to encode with.</param>
        /// <returns>A byte array of a encoded data.</returns>
        public static byte[] Encrypt(byte[] data, byte[] hash)
        {
            using Aes aes = Aes.Create();
            aes.BlockSize = 128;
            aes.KeySize = 256;
            aes.Key = hash.Take(32).ToArray();
            aes.IV = hash.Take(16).ToArray();
            aes.Padding = PaddingMode.PKCS7;
            aes.Mode = CipherMode.CBC;

            using ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            byte[] encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);
            return encrypted;
        }

        /// <summary>
        /// Decrypts an array of bytes with a hash.
        /// </summary>
        /// <param name="data">The data to be decoded.</param>
        /// <param name="hash">The hash to decode with.</param>
        /// <returns>A byte array of a decoded data.</returns>
        public static byte[] Decrypt(byte[] data, byte[] hash)
        {
            using Aes aes = Aes.Create();
            aes.BlockSize = 128;
            aes.KeySize = 256;
            aes.Key = hash.Take(32).ToArray();
            aes.IV = hash.Take(16).ToArray();
            aes.Padding = PaddingMode.PKCS7;
            aes.Mode = CipherMode.CBC;

            using ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            byte[] decrypted = decryptor.TransformFinalBlock(data, 0, data.Length);
            return decrypted;
        }
    }
}
