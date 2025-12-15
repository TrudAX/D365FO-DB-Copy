namespace DBCopyTool.Helpers
{
    /// <summary>
    /// Helper methods for working with SQL Server timestamps (SysRowVersion)
    /// </summary>
    public static class TimestampHelper
    {
        /// <summary>
        /// Compares two SQL Server timestamps (binary(8))
        /// Returns: -1 if a < b, 0 if equal, 1 if a > b
        /// </summary>
        public static int CompareTimestamp(byte[]? a, byte[]? b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            for (int i = 0; i < 8; i++)
            {
                if (a[i] < b[i]) return -1;
                if (a[i] > b[i]) return 1;
            }
            return 0;
        }

        /// <summary>
        /// Returns the minimum of two timestamps
        /// </summary>
        public static byte[]? MinTimestamp(byte[]? a, byte[]? b)
        {
            return CompareTimestamp(a, b) <= 0 ? a : b;
        }

        /// <summary>
        /// Converts timestamp to hex string for storage
        /// </summary>
        public static string ToHexString(byte[]? timestamp)
        {
            if (timestamp == null) return "";
            return "0x" + BitConverter.ToString(timestamp).Replace("-", "");
        }

        /// <summary>
        /// Parses hex string back to timestamp
        /// </summary>
        public static byte[]? FromHexString(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;

            // Remove "0x" or "0X" prefix if present
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                hex = hex.Substring(2);
            }

            if (hex.Length != 16) return null;

            byte[] bytes = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}
