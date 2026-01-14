namespace DBSyncTool.Models
{
    /// <summary>
    /// In-memory cache for SQLDICTIONARY data to avoid repeated database queries
    /// </summary>
    public class SqlDictionaryCache
    {
        /// <summary>
        /// TableName (uppercase) -> TableID mapping
        /// Used to quickly find TableID by table name
        /// </summary>
        public Dictionary<string, int> TableNameToId { get; set; }

        /// <summary>
        /// TableID -> List of Field Names mapping
        /// Used to quickly get all fields for a table
        /// </summary>
        public Dictionary<int, List<string>> TableIdToFields { get; set; }

        public SqlDictionaryCache()
        {
            TableNameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            TableIdToFields = new Dictionary<int, List<string>>();
        }

        /// <summary>
        /// Gets TableID for a given table name (case-insensitive)
        /// </summary>
        public int? GetTableId(string tableName)
        {
            return TableNameToId.TryGetValue(tableName, out int tableId) ? tableId : null;
        }

        /// <summary>
        /// Gets list of field names for a given TableID
        /// </summary>
        public List<string>? GetFields(int tableId)
        {
            return TableIdToFields.TryGetValue(tableId, out var fields) ? fields : null;
        }

        /// <summary>
        /// Returns statistics about the cache
        /// </summary>
        public string GetStats()
        {
            return $"{TableNameToId.Count} tables, {TableIdToFields.Count} field lists";
        }
    }
}
