using DBSyncTool.Models;

namespace DBSyncTool.Helpers
{
    public enum StrategyChangeKind
    {
        Added,
        Modified,
        Removed
    }

    /// <summary>
    /// A single Copy strategy line that was added, modified or removed by the user.
    /// </summary>
    public class StrategyChange
    {
        public string TableName { get; init; } = string.Empty;
        public StrategyChangeKind Kind { get; init; }
    }

    /// <summary>
    /// Saved values (SysRowVersion timestamps + MaxRecId) removed for one table.
    /// </summary>
    public class SavedValuesRemoval
    {
        public string TableName { get; init; } = string.Empty;
        public bool Tier2Timestamp { get; init; }
        public bool AxDbTimestamp { get; init; }
        public bool MaxRecId { get; init; }

        public string Describe()
        {
            var parts = new List<string>();
            if (Tier2Timestamp) parts.Add("Tier2 timestamp");
            if (AxDbTimestamp) parts.Add("AxDB timestamp");
            if (MaxRecId) parts.Add("MaxRecId");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Detects Copy strategy edits and removes the stored optimization values for the
    /// affected tables. Without this, a changed strategy (for example a larger record
    /// count) would not be applied on the next run: the stored timestamps keep the table
    /// in INCREMENTAL mode, which fetches only rows newer than the saved timestamp.
    /// </summary>
    public static class SavedValuesHelper
    {
        /// <summary>
        /// Table name of a strategy line: "CUSTTABLE|5000 -truncate" -> "CUSTTABLE"
        /// </summary>
        public static string GetStrategyTableName(string line)
        {
            var l = line.Trim();
            if (l.EndsWith(" -truncate", StringComparison.OrdinalIgnoreCase))
                l = l.Substring(0, l.Length - 10).Trim();
            return l.Split('|')[0].Trim();
        }

        /// <summary>
        /// Compares two Copy strategy texts and returns the tables whose strategy line was
        /// added, modified or removed. Order-independent, so sorting the list reports nothing.
        /// </summary>
        public static List<StrategyChange> GetStrategyChanges(string? oldText, string? newText)
        {
            var oldLines = ParseStrategyLines(oldText);
            var newLines = ParseStrategyLines(newText);
            var changes = new List<StrategyChange>();

            foreach (var kvp in newLines)
            {
                if (!oldLines.TryGetValue(kvp.Key, out var oldLine))
                {
                    changes.Add(new StrategyChange { TableName = kvp.Key, Kind = StrategyChangeKind.Added });
                }
                else if (!string.Equals(oldLine, kvp.Value, StringComparison.Ordinal))
                {
                    changes.Add(new StrategyChange { TableName = kvp.Key, Kind = StrategyChangeKind.Modified });
                }
            }

            foreach (var kvp in oldLines)
            {
                if (!newLines.ContainsKey(kvp.Key))
                {
                    changes.Add(new StrategyChange { TableName = kvp.Key, Kind = StrategyChangeKind.Removed });
                }
            }

            return changes.OrderBy(c => c.TableName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Removes the stored Tier2/AxDB timestamps and MaxRecIds for the given tables from
        /// the configuration. Returns one entry per table that actually had a saved value.
        /// </summary>
        public static List<SavedValuesRemoval> RemoveSavedValues(AppConfiguration config, IEnumerable<string> tableNames)
        {
            var tables = new HashSet<string>(
                tableNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (tables.Count == 0) return new List<SavedValuesRemoval>();

            config.Tier2Timestamps = RemoveTableLines(config.Tier2Timestamps, tables, out var tier2Removed);
            config.AxDBTimestamps = RemoveTableLines(config.AxDBTimestamps, tables, out var axdbRemoved);
            config.MaxTransferredRecIds = RemoveTableLines(config.MaxTransferredRecIds, tables, out var maxRecIdRemoved);

            var affected = new HashSet<string>(tier2Removed, StringComparer.OrdinalIgnoreCase);
            affected.UnionWith(axdbRemoved);
            affected.UnionWith(maxRecIdRemoved);

            return affected
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(t => new SavedValuesRemoval
                {
                    TableName = t,
                    Tier2Timestamp = tier2Removed.Contains(t, StringComparer.OrdinalIgnoreCase),
                    AxDbTimestamp = axdbRemoved.Contains(t, StringComparer.OrdinalIgnoreCase),
                    MaxRecId = maxRecIdRemoved.Contains(t, StringComparer.OrdinalIgnoreCase)
                })
                .ToList();
        }

        /// <summary>
        /// Strategy lines keyed by table name (case-insensitive). Last line wins for duplicates.
        /// </summary>
        private static Dictionary<string, string> ParseStrategyLines(string? text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var tableName = GetStrategyTableName(trimmed);
                if (tableName.Length == 0) continue;

                result[tableName] = trimmed;
            }
            return result;
        }

        /// <summary>
        /// Drops "TABLENAME,value" lines whose table is in the set, keeping everything else as is.
        /// </summary>
        private static string RemoveTableLines(string? text, HashSet<string> tables, out List<string> removed)
        {
            removed = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;

            var kept = new List<string>();
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var tableName = trimmed.Split(',')[0].Trim();
                if (tables.Contains(tableName))
                {
                    removed.Add(tableName);
                }
                else
                {
                    kept.Add(trimmed);
                }
            }

            return string.Join("\r\n", kept);
        }
    }
}
