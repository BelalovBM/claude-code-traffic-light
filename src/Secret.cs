using System;
using System.Security.Cryptography;
using System.Text;

namespace Semaphore
{
    // Secrets (tokens, passwords) are stored encrypted with the Windows account key,
    // so a copied config.json is useless on another machine or account.
    static class Secret
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeCodeTrafficLight");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch { return ""; }
        }

        public static string RandomTopic(string prefix = "ccl-")
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
            var bytes = new byte[24];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            var sb = new StringBuilder(prefix);
            foreach (byte b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            return sb.ToString();
        }

        // One-time token for a single approval request: 128 random bits as hex.
        public static string RandomToken()
        {
            var bytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
