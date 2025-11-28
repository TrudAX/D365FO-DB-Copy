using System.Data;

namespace DBCopyTool.Models
{
    public class TableInfo
    {
        // Identification
        public string TableName { get; set; } = string.Empty;
        public int TableId { get; set; }

        // Strategy
        public CopyStrategyType StrategyType { get; set; }
        public int StrategyValue { get; set; }

        // Tier2 Info
        public long Tier2RowCount { get; set; }
        public decimal Tier2SizeGB { get; set; }
        public string FetchSql { get; set; } = string.Empty;

        // Field Info
        public List<string> CopyableFields { get; set; } = new List<string>();

        // Execution State
        public TableStatus Status { get; set; }
        public int RecordsFetched { get; set; }
        public long MinRecId { get; set; }
        public decimal FetchTimeSeconds { get; set; }
        public decimal InsertTimeSeconds { get; set; }
        public string Error { get; set; } = string.Empty;

        // Cached Data (not persisted)
        public DataTable? CachedData { get; set; }

        // Display properties for DataGridView
        public string StrategyDisplay => StrategyType == CopyStrategyType.RecId
            ? $"RecId:{StrategyValue}"
            : $"Days:{StrategyValue}";

        public string Tier2SizeGBDisplay => Tier2SizeGB.ToString("F2");
        public string FetchTimeDisplay => FetchTimeSeconds > 0 ? FetchTimeSeconds.ToString("F2") : "";
        public string InsertTimeDisplay => InsertTimeSeconds > 0 ? InsertTimeSeconds.ToString("F2") : "";
        public string Tier2RowCountDisplay => Tier2RowCount.ToString("N0");
    }

    public enum TableStatus
    {
        Pending,
        Fetching,
        Fetched,
        FetchError,
        Inserting,
        Inserted,
        InsertError
    }

    public enum CopyStrategyType
    {
        RecId,
        ModifiedDate
    }
}
