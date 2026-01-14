using DBSyncTool.Models;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// Manages timestamp storage for SysRowVersion optimization
    /// </summary>
    public class TimestampManager
    {
        private Dictionary<string, byte[]> _tier2Timestamps = new();
        private Dictionary<string, byte[]> _axdbTimestamps = new();

        public void LoadFromConfig(AppConfiguration config)
        {
            _tier2Timestamps = ParseTimestampText(config.Tier2Timestamps);
            _axdbTimestamps = ParseTimestampText(config.AxDBTimestamps);
        }

        public void SaveToConfig(AppConfiguration config)
        {
            config.Tier2Timestamps = FormatTimestampText(_tier2Timestamps);
            config.AxDBTimestamps = FormatTimestampText(_axdbTimestamps);
        }

        public byte[]? GetTier2Timestamp(string tableName)
        {
            return _tier2Timestamps.TryGetValue(tableName.ToUpper(), out var ts) ? ts : null;
        }

        public byte[]? GetAxDBTimestamp(string tableName)
        {
            return _axdbTimestamps.TryGetValue(tableName.ToUpper(), out var ts) ? ts : null;
        }

        public void SetTimestamps(string tableName, byte[] tier2Timestamp, byte[] axdbTimestamp)
        {
            _tier2Timestamps[tableName.ToUpper()] = tier2Timestamp;
            _axdbTimestamps[tableName.ToUpper()] = axdbTimestamp;
        }

        public void ClearTable(string tableName)
        {
            _tier2Timestamps.Remove(tableName.ToUpper());
            _axdbTimestamps.Remove(tableName.ToUpper());
        }

        public void ClearAll()
        {
            _tier2Timestamps.Clear();
            _axdbTimestamps.Clear();
        }

        private Dictionary<string, byte[]> ParseTimestampText(string text)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (var line in text.Split('\n', '\r'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var parts = trimmed.Split(',');
                if (parts.Length == 2)
                {
                    var tableName = parts[0].Trim();
                    var timestamp = TimestampHelper.FromHexString(parts[1].Trim());
                    if (timestamp != null)
                    {
                        result[tableName] = timestamp;
                    }
                }
            }
            return result;
        }

        private string FormatTimestampText(Dictionary<string, byte[]> timestamps)
        {
            var lines = timestamps
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => $"{kvp.Key},{TimestampHelper.ToHexString(kvp.Value)}");
            return string.Join("\r\n", lines);
        }
    }
}
