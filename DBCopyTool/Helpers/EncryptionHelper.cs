using System.Text;

namespace DBCopyTool.Helpers
{
    public static class EncryptionHelper
    {
        /// <summary>
        /// Obfuscates a password using Base64 encoding
        /// Note: This is for obfuscation only, not security
        /// </summary>
        public static string ObfuscatePassword(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainText);
                return Convert.ToBase64String(plainTextBytes);
            }
            catch
            {
                return plainText;
            }
        }

        /// <summary>
        /// Deobfuscates a password from Base64 encoding
        /// </summary>
        public static string DeobfuscatePassword(string obfuscatedText)
        {
            if (string.IsNullOrEmpty(obfuscatedText))
                return string.Empty;

            try
            {
                byte[] base64EncodedBytes = Convert.FromBase64String(obfuscatedText);
                return Encoding.UTF8.GetString(base64EncodedBytes);
            }
            catch
            {
                // If it's not valid Base64, assume it's plain text
                return obfuscatedText;
            }
        }
    }
}
