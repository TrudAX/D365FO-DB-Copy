using System.Data;
using Microsoft.Data.SqlClient;
using DBCopyTool.Models;

namespace DBCopyTool.Services
{
    public class AxDbDataService
    {
        private readonly ConnectionSettings _connectionSettings;
        private readonly string _connectionString;
        private readonly Action<string> _logger;

        public AxDbDataService(ConnectionSettings connectionSettings, Action<string> logger)
        {
            _connectionSettings = connectionSettings;
            _connectionString = connectionSettings.BuildConnectionString(isAzure: false);
            _logger = logger;
        }

        /// <summary>
        /// Tests the connection to AxDB database
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection.State == ConnectionState.Open;
        }

        /// <summary>
        /// Gets the TableID for a table from SQLDICTIONARY
        /// </summary>
        public async Task<int?> GetTableIdAsync(string tableName)
        {
            const string query = @"
                SELECT TableID
                FROM SQLDICTIONARY
                WHERE UPPER(name) = UPPER(@TableName)
                  AND FIELDID = 0";

            _logger($"[AxDB SQL] Getting TableID for {tableName}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TableName", tableName);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            var result = await command.ExecuteScalarAsync();

            return result != null ? Convert.ToInt32(result) : null;
        }

        /// <summary>
        /// Gets the field names for a table from SQLDICTIONARY
        /// </summary>
        public async Task<List<string>> GetTableFieldsAsync(int tableId)
        {
            const string query = @"
                SELECT SQLName
                FROM SQLDICTIONARY
                WHERE TableID = @TableId
                  AND FIELDID <> 0";

            _logger($"[AxDB SQL] Getting fields for TableID {tableId}");

            var fields = new List<string>();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TableId", tableId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                fields.Add(reader.GetString(0));
            }

            return fields;
        }

        /// <summary>
        /// Inserts data into a table using SqlBulkCopy
        /// Handles deletes, trigger disabling, bulk insert, trigger enabling, and sequence update
        /// </summary>
        public async Task<int> InsertDataAsync(TableInfo tableInfo, CancellationToken cancellationToken)
        {
            if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
                return 0;

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            using var transaction = connection.BeginTransaction();

            try
            {
                _logger($"[AxDB] Starting insert for table {tableInfo.TableName} ({tableInfo.CachedData.Rows.Count} rows)");

                // Step 1: Delete existing records based on strategy
                await DeleteExistingRecordsAsync(tableInfo, connection, transaction, cancellationToken);

                // Step 2: Disable triggers
                string disableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] DISABLE TRIGGER ALL";
                _logger($"[AxDB SQL] {disableTriggersSql}");
                await ExecuteNonQueryAsync(disableTriggersSql, connection, transaction);

                // Step 3: Bulk insert data
                _logger($"[AxDB] Bulk inserting {tableInfo.CachedData.Rows.Count} rows into {tableInfo.TableName}");
                using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction))
                {
                    bulkCopy.DestinationTableName = tableInfo.TableName;
                    bulkCopy.BatchSize = 10000;
                    bulkCopy.BulkCopyTimeout = _connectionSettings.CommandTimeout;

                    // Map columns
                    foreach (DataColumn column in tableInfo.CachedData.Columns)
                    {
                        bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    }

                    await bulkCopy.WriteToServerAsync(tableInfo.CachedData, cancellationToken);
                }

                // Step 4: Enable triggers (always, even if errors occur)
                string enableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL";
                _logger($"[AxDB SQL] {enableTriggersSql}");
                await ExecuteNonQueryAsync(enableTriggersSql, connection, transaction);

                // Step 5: Update sequence
                await UpdateSequenceAsync(tableInfo, connection, transaction, cancellationToken);

                // Commit transaction
                await transaction.CommitAsync(cancellationToken);

                return tableInfo.CachedData.Rows.Count;
            }
            catch
            {
                // Always try to re-enable triggers on error
                try
                {
                    await ExecuteNonQueryAsync($"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL", connection, transaction);
                }
                catch
                {
                    // Ignore errors when re-enabling triggers
                }

                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        /// <summary>
        /// Deletes existing records based on the copy strategy
        /// If copying the whole table (CachedData.Rows.Count >= Tier2RowCount), uses TRUNCATE for performance
        /// </summary>
        private async Task DeleteExistingRecordsAsync(TableInfo tableInfo, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
        {
            if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
                return;

            // Optimization: If we're copying the whole table, truncate it for better performance
            if (tableInfo.CachedData.Rows.Count >= tableInfo.Tier2RowCount)
            {
                string truncateQuery = $"TRUNCATE TABLE [{tableInfo.TableName}]";
                _logger($"[AxDB SQL] Truncating table (full copy): {truncateQuery}");

                using var command = new SqlCommand(truncateQuery, connection, transaction);
                command.CommandTimeout = _connectionSettings.CommandTimeout;
                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            if (tableInfo.StrategyType == CopyStrategyType.RecId)
            {
                // Delete by RecId
                long minRecId = GetMinRecId(tableInfo.CachedData);
                string deleteQuery = $"DELETE FROM [{tableInfo.TableName}] WHERE RecId >= @MinRecId";
                _logger($"[AxDB SQL] Deleting records: {deleteQuery} (MinRecId={minRecId})");

                using var command = new SqlCommand(deleteQuery, connection, transaction);
                command.Parameters.AddWithValue("@MinRecId", minRecId);
                command.CommandTimeout = _connectionSettings.CommandTimeout;

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            else if (tableInfo.StrategyType == CopyStrategyType.ModifiedDate)
            {
                // Delete by ModifiedDate
                DateTime minModifiedDate = GetMinModifiedDate(tableInfo.CachedData);
                string deleteDateQuery = $"DELETE FROM [{tableInfo.TableName}] WHERE [MODIFIEDDATETIME] >= @MinModifiedDate";
                _logger($"[AxDB SQL] Deleting by date: {deleteDateQuery} (MinDate={minModifiedDate:yyyy-MM-dd HH:mm:ss})");

                using var command1 = new SqlCommand(deleteDateQuery, connection, transaction);
                command1.Parameters.AddWithValue("@MinModifiedDate", minModifiedDate);
                command1.CommandTimeout = _connectionSettings.CommandTimeout;
                await command1.ExecuteNonQueryAsync(cancellationToken);

                // Delete by RecId list (handles records modified in Tier2 that had old dates in AxDB)
                var recIds = GetRecIdList(tableInfo.CachedData);
                if (recIds.Count > 0)
                {
                    _logger($"[AxDB] Deleting {recIds.Count} records by RecId list");
                    // Split into batches of 1000 to avoid SQL parameter limits
                    for (int i = 0; i < recIds.Count; i += 1000)
                    {
                        var batch = recIds.Skip(i).Take(1000).ToList();
                        string recIdList = string.Join(",", batch);
                        string deleteRecIdQuery = $"DELETE FROM [{tableInfo.TableName}] WHERE RecId IN ({recIdList})";
                        _logger($"[AxDB SQL] {deleteRecIdQuery}");

                        using var command2 = new SqlCommand(deleteRecIdQuery, connection, transaction);
                        command2.CommandTimeout = _connectionSettings.CommandTimeout;
                        await command2.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
            }
        }

        /// <summary>
        /// Updates the sequence for a table if needed
        /// </summary>
        private async Task UpdateSequenceAsync(TableInfo tableInfo, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
        {
            if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
                return;

            // Get max RecId from the table
            string maxRecIdQuery = $"SELECT MAX(RecId) FROM [{tableInfo.TableName}]";
            _logger($"[AxDB SQL] {maxRecIdQuery}");
            using var command1 = new SqlCommand(maxRecIdQuery, connection, transaction);
            command1.CommandTimeout = _connectionSettings.CommandTimeout;
            var maxRecIdResult = await command1.ExecuteScalarAsync(cancellationToken);

            if (maxRecIdResult == null || maxRecIdResult == DBNull.Value)
                return;

            long maxRecId = Convert.ToInt64(maxRecIdResult);

            // Get current sequence value
            string sequenceName = $"SEQ_{tableInfo.TableId}";
            string currentSeqQuery = $"SELECT CAST(current_value AS BIGINT) FROM sys.sequences WHERE name = @SequenceName";
            _logger($"[AxDB SQL] Getting sequence value for {sequenceName}");

            using var command2 = new SqlCommand(currentSeqQuery, connection, transaction);
            command2.Parameters.AddWithValue("@SequenceName", sequenceName);
            command2.CommandTimeout = _connectionSettings.CommandTimeout;
            var currentSeqResult = await command2.ExecuteScalarAsync(cancellationToken);

            if (currentSeqResult == null || currentSeqResult == DBNull.Value)
                return;

            long currentSeq = Convert.ToInt64(currentSeqResult);

            // Update sequence if max RecId is higher
            if (maxRecId > currentSeq)
            {
                string updateSeqQuery = $"ALTER SEQUENCE [{sequenceName}] RESTART WITH {maxRecId}";
                _logger($"[AxDB SQL] {updateSeqQuery}");
                using var command3 = new SqlCommand(updateSeqQuery, connection, transaction);
                command3.CommandTimeout = _connectionSettings.CommandTimeout;
                await command3.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Executes a non-query command
        /// </summary>
        private async Task ExecuteNonQueryAsync(string query, SqlConnection connection, SqlTransaction transaction)
        {
            using var command = new SqlCommand(query, connection, transaction);
            command.CommandTimeout = _connectionSettings.CommandTimeout;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Loads entire SQLDICTIONARY into memory cache for fast lookups
        /// This dramatically reduces query count from ~4000 to 1 during PrepareTableList
        /// </summary>
        public async Task<SqlDictionaryCache> LoadSqlDictionaryCacheAsync()
        {
            const string query = @"
                SELECT name, TableID, FIELDID, SQLName
                FROM SQLDICTIONARY
                ORDER BY TableID, FIELDID";

            _logger("[AxDB] Loading SQLDICTIONARY cache...");
            _logger($"[AxDB SQL] {query}");

            var cache = new SqlDictionaryCache();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                int tableId = reader.GetInt32(1);
                int fieldId = reader.GetInt32(2);
                string sqlName = reader.GetString(3);

                if (fieldId == 0)
                {
                    // This is a table entry
                    cache.TableNameToId[name.ToUpper()] = tableId;
                }
                else
                {
                    // This is a field entry
                    if (!cache.TableIdToFields.ContainsKey(tableId))
                    {
                        cache.TableIdToFields[tableId] = new List<string>();
                    }
                    cache.TableIdToFields[tableId].Add(sqlName);
                }
            }

            _logger($"[AxDB] Loaded SQLDICTIONARY cache: {cache.GetStats()}");

            return cache;
        }

        /// <summary>
        /// Gets the minimum RecId from a DataTable
        /// </summary>
        private long GetMinRecId(DataTable data)
        {
            if (!data.Columns.Contains("RecId"))
                return 0;

            long minRecId = long.MaxValue;
            foreach (DataRow row in data.Rows)
            {
                if (row["RecId"] != DBNull.Value)
                {
                    long recId = Convert.ToInt64(row["RecId"]);
                    if (recId < minRecId)
                        minRecId = recId;
                }
            }

            return minRecId == long.MaxValue ? 0 : minRecId;
        }

        /// <summary>
        /// Gets the minimum ModifiedDateTime from a DataTable
        /// </summary>
        private DateTime GetMinModifiedDate(DataTable data)
        {
            if (!data.Columns.Contains("MODIFIEDDATETIME"))
                return DateTime.MinValue;

            DateTime minDate = DateTime.MaxValue;
            foreach (DataRow row in data.Rows)
            {
                if (row["MODIFIEDDATETIME"] != DBNull.Value)
                {
                    DateTime date = Convert.ToDateTime(row["MODIFIEDDATETIME"]);
                    if (date < minDate)
                        minDate = date;
                }
            }

            return minDate == DateTime.MaxValue ? DateTime.MinValue : minDate;
        }

        /// <summary>
        /// Gets the list of RecIds from a DataTable
        /// </summary>
        private List<long> GetRecIdList(DataTable data)
        {
            var recIds = new List<long>();

            if (!data.Columns.Contains("RecId"))
                return recIds;

            foreach (DataRow row in data.Rows)
            {
                if (row["RecId"] != DBNull.Value)
                {
                    recIds.Add(Convert.ToInt64(row["RecId"]));
                }
            }

            return recIds;
        }
    }
}
