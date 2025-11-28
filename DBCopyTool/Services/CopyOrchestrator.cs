using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DBCopyTool.Models;

namespace DBCopyTool.Services
{
    public class CopyOrchestrator
    {
        private readonly AppConfiguration _config;
        private readonly Tier2DataService _tier2Service;
        private readonly AxDbDataService _axDbService;
        private readonly Action<string> _logger;

        private List<TableInfo> _tables = new List<TableInfo>();
        private CancellationTokenSource? _cancellationTokenSource;

        public event EventHandler<List<TableInfo>>? TablesUpdated;
        public event EventHandler<string>? StatusUpdated;

        public CopyOrchestrator(AppConfiguration config, Action<string> logger)
        {
            _config = config;
            _tier2Service = new Tier2DataService(config.Tier2Connection, logger);
            _axDbService = new AxDbDataService(config.AxDbConnection, logger);
            _logger = logger;
        }

        public List<TableInfo> GetTables() => _tables;

        /// <summary>
        /// Stage 1: Prepare Table List
        /// </summary>
        public async Task PrepareTableListAsync()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                _logger("Starting Prepare Table List...");
                _tables.Clear();

                // Parse and validate strategy overrides
                var strategyOverrides = ParseStrategyOverrides(_config.StrategyOverrides);

                // ========== LOAD SQLDICTIONARY CACHES ONCE ==========
                _logger("─────────────────────────────────────────────");
                var tier2Cache = await _tier2Service.LoadSqlDictionaryCacheAsync();
                var axDbCache = await _axDbService.LoadSqlDictionaryCacheAsync();
                _logger("─────────────────────────────────────────────");
                // ====================================================

                // Discover tables from Tier2
                _logger("Discovering tables from Tier2...");
                var discoveredTables = await _tier2Service.DiscoverTablesAsync();
                _logger($"Discovered {discoveredTables.Count} tables");

                // Get inclusion and exclusion patterns
                var inclusionPatterns = GetPatterns(_config.TablesToInclude);
                var exclusionPatterns = GetPatterns(_config.TablesToExclude);
                var excludedFields = GetExcludedFieldsMap(_config.FieldsToExclude);

                int skipped = 0;
                int processed = 0;

                foreach (var (tableName, rowCount, sizeGB, bytesPerRow) in discoveredTables)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Apply inclusion patterns
                    if (!MatchesAnyPattern(tableName, inclusionPatterns))
                        continue;

                    // Apply exclusion patterns
                    if (MatchesAnyPattern(tableName, exclusionPatterns))
                    {
                        skipped++;
                        continue;
                    }

                    // ========== USE CACHE INSTEAD OF DATABASE QUERIES ==========
                    // Get TableID from Tier2 cache (no database query!)
                    var tier2TableId = tier2Cache.GetTableId(tableName);
                    if (tier2TableId == null)
                    {
                        _logger($"Table {tableName} not found in Tier2 SQLDICTIONARY, skipping");
                        skipped++;
                        continue;
                    }

                    // Get TableID from AxDB cache (no database query!)
                    var axDbTableId = axDbCache.GetTableId(tableName);
                    if (axDbTableId == null)
                    {
                        _logger($"Table {tableName} not found in AxDB SQLDICTIONARY, skipping");
                        skipped++;
                        continue;
                    }

                    // Determine copy strategy
                    var (strategyType, strategyValue) = GetStrategy(tableName, strategyOverrides);

                    // Get fields from caches (no database queries!)
                    var tier2Fields = tier2Cache.GetFields(tier2TableId.Value) ?? new List<string>();
                    var axDbFields = axDbCache.GetFields(axDbTableId.Value) ?? new List<string>();
                    // ===========================================================

                    // Calculate copyable fields (intersection minus excluded)
                    var copyableFields = tier2Fields.Intersect(axDbFields, StringComparer.OrdinalIgnoreCase).ToList();
                    var tableExcludedFields = excludedFields.ContainsKey(tableName.ToUpper())
                        ? excludedFields[tableName.ToUpper()]
                        : new List<string>();
                    var globalExcludedFields = excludedFields.ContainsKey("")
                        ? excludedFields[""]
                        : new List<string>();

                    copyableFields = copyableFields
                        .Where(f => !tableExcludedFields.Contains(f.ToUpper()))
                        .Where(f => !globalExcludedFields.Contains(f.ToUpper()))
                        .ToList();

                    if (copyableFields.Count == 0)
                    {
                        _logger($"Table {tableName} has no copyable fields, skipping");
                        skipped++;
                        continue;
                    }

                    // Verify ModifiedDate strategy requirements
                    if (strategyType == CopyStrategyType.ModifiedDate)
                    {
                        if (!copyableFields.Any(f => f.Equals("MODIFIEDDATETIME", StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger($"Table {tableName} configured for ModifiedDate strategy but lacks MODIFIEDDATETIME column");

                            var errorTable = new TableInfo
                            {
                                TableName = tableName,
                                TableId = tier2TableId.Value,
                                Status = TableStatus.FetchError,
                                Error = "MODIFIEDDATETIME column not found",
                                Tier2RowCount = rowCount,
                                Tier2SizeGB = sizeGB
                            };
                            _tables.Add(errorTable);
                            skipped++;
                            continue;
                        }
                    }

                    // Generate fetch SQL
                    string fetchSql = GenerateFetchSql(tableName, copyableFields, strategyType, strategyValue);

                    // Create TableInfo
                    var tableInfo = new TableInfo
                    {
                        TableName = tableName,
                        TableId = tier2TableId.Value,
                        StrategyType = strategyType,
                        StrategyValue = strategyValue,
                        Tier2RowCount = rowCount,
                        Tier2SizeGB = sizeGB,
                        FetchSql = fetchSql,
                        CopyableFields = copyableFields,
                        Status = TableStatus.Pending
                    };

                    _tables.Add(tableInfo);
                    processed++;
                }

                _logger($"Prepared {processed} tables, {skipped} skipped");
                OnStatusUpdated($"Prepared {processed} tables, {skipped} skipped");
                OnTablesUpdated();
            }
            catch (OperationCanceledException)
            {
                _logger("Prepare Table List cancelled");
                OnStatusUpdated("Cancelled");
            }
            catch (Exception ex)
            {
                _logger($"ERROR: {ex.Message}");
                OnStatusUpdated($"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stage 2: Get Data
        /// </summary>
        public async Task GetDataAsync()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                _logger("─────────────────────────────────────────────");
                _logger("Starting Get Data...");

                var pendingTables = _tables.Where(t => t.Status == TableStatus.Pending).ToList();
                if (pendingTables.Count == 0)
                {
                    _logger("No pending tables to fetch");
                    return;
                }

                int completed = 0;
                int failed = 0;
                var semaphore = new SemaphoreSlim(_config.ParallelFetchConnections);

                var tasks = pendingTables.Select(async table =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await FetchTableDataAsync(table, cancellationToken);
                        if (table.Status == TableStatus.Fetched)
                            Interlocked.Increment(ref completed);
                        else
                            Interlocked.Increment(ref failed);

                        OnStatusUpdated($"Stage 2/3: Get Data - {completed + failed}/{pendingTables.Count} tables");
                    }
                    finally
                    {
                        semaphore.Release();
                        OnTablesUpdated();
                    }
                });

                await Task.WhenAll(tasks);

                _logger($"Fetched {completed} tables, {failed} failed");
                OnStatusUpdated($"Fetched {completed} tables, {failed} failed");
            }
            catch (OperationCanceledException)
            {
                _logger("Get Data cancelled");
                OnStatusUpdated("Cancelled");
            }
            catch (Exception ex)
            {
                _logger($"ERROR: {ex.Message}");
                OnStatusUpdated($"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stage 3: Insert Data
        /// </summary>
        public async Task InsertDataAsync()
        {
            await InsertDataInternalAsync(false);
        }

        /// <summary>
        /// Stage 4: Insert Failed (Retry)
        /// </summary>
        public async Task InsertFailedAsync()
        {
            await InsertDataInternalAsync(true);
        }

        /// <summary>
        /// Internal method to insert data (for both Stage 3 and Stage 4)
        /// </summary>
        private async Task InsertDataInternalAsync(bool retryOnly)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                _logger("─────────────────────────────────────────────");
                string stageName = retryOnly ? "Insert Failed (Retry)" : "Insert Data";
                _logger($"Starting {stageName}...");

                var tablesToInsert = retryOnly
                    ? _tables.Where(t => t.Status == TableStatus.InsertError).ToList()
                    : _tables.Where(t => t.Status == TableStatus.Fetched).ToList();

                if (tablesToInsert.Count == 0)
                {
                    _logger($"No tables to insert");
                    return;
                }

                int completed = 0;
                int failed = 0;
                var semaphore = new SemaphoreSlim(_config.ParallelInsertConnections);

                var tasks = tablesToInsert.Select(async table =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await InsertTableDataAsync(table, cancellationToken);
                        if (table.Status == TableStatus.Inserted)
                            Interlocked.Increment(ref completed);
                        else
                            Interlocked.Increment(ref failed);

                        OnStatusUpdated($"Stage 3/3: Insert Data - {completed + failed}/{tablesToInsert.Count} tables");
                    }
                    finally
                    {
                        semaphore.Release();
                        OnTablesUpdated();
                    }
                });

                await Task.WhenAll(tasks);

                _logger($"Inserted {completed} tables, {failed} failed");
                OnStatusUpdated($"Inserted {completed} tables, {failed} failed");
            }
            catch (OperationCanceledException)
            {
                _logger("Insert Data cancelled");
                OnStatusUpdated("Cancelled");
            }
            catch (Exception ex)
            {
                _logger($"ERROR: {ex.Message}");
                OnStatusUpdated($"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Run all stages sequentially
        /// </summary>
        public async Task RunAllStagesAsync()
        {
            try
            {
                await PrepareTableListAsync();
                await GetDataAsync();
                await InsertDataAsync();
            }
            catch (Exception ex)
            {
                _logger($"ERROR in Run All: {ex.Message}");
                OnStatusUpdated($"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stops the current operation
        /// </summary>
        public void Stop()
        {
            _cancellationTokenSource?.Cancel();
            _logger("Stop requested");
        }

        // ========== Helper Methods ==========

        private async Task FetchTableDataAsync(TableInfo table, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            table.Status = TableStatus.Fetching;
            OnTablesUpdated();

            try
            {
                DataTable data;
                if (table.StrategyType == CopyStrategyType.RecId)
                {
                    data = await _tier2Service.FetchDataByRecIdAsync(
                        table.TableName,
                        table.CopyableFields,
                        table.StrategyValue,
                        cancellationToken);
                }
                else
                {
                    data = await _tier2Service.FetchDataByModifiedDateAsync(
                        table.TableName,
                        table.CopyableFields,
                        table.StrategyValue,
                        cancellationToken);
                }

                table.CachedData = data;
                table.RecordsFetched = data.Rows.Count;
                table.MinRecId = GetMinRecIdFromData(data);
                table.FetchTimeSeconds = (decimal)stopwatch.Elapsed.TotalSeconds;
                table.Status = TableStatus.Fetched;

                _logger($"Fetched {table.TableName}: {table.RecordsFetched} records in {table.FetchTimeSeconds:F2}s");
            }
            catch (Exception ex)
            {
                table.Status = TableStatus.FetchError;
                table.Error = ex.Message;
                _logger($"ERROR fetching {table.TableName}: {ex.Message}");
            }
        }

        private async Task InsertTableDataAsync(TableInfo table, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            table.Status = TableStatus.Inserting;
            OnTablesUpdated();

            try
            {
                await _axDbService.InsertDataAsync(table, cancellationToken);

                table.InsertTimeSeconds = (decimal)stopwatch.Elapsed.TotalSeconds;
                table.Status = TableStatus.Inserted;
                table.Error = string.Empty;

                _logger($"Inserted {table.TableName}: {table.RecordsFetched} records in {table.InsertTimeSeconds:F2}s");
            }
            catch (Exception ex)
            {
                table.Status = TableStatus.InsertError;
                table.Error = ex.Message;
                _logger($"ERROR inserting {table.TableName}: {ex.Message}");
            }
        }

        private long GetMinRecIdFromData(DataTable data)
        {
            if (data.Rows.Count == 0 || !data.Columns.Contains("RecId"))
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

        private Dictionary<string, (CopyStrategyType, int)> ParseStrategyOverrides(string overrides)
        {
            var result = new Dictionary<string, (CopyStrategyType, int)>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(overrides))
                return result;

            var lines = overrides.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                var parts = trimmed.Split(':');
                if (parts.Length == 2)
                {
                    // Format: TableName:RecordCount
                    string tableName = parts[0].Trim();
                    if (int.TryParse(parts[1].Trim(), out int recordCount))
                    {
                        result[tableName] = (CopyStrategyType.RecId, recordCount);
                    }
                }
                else if (parts.Length == 3 && parts[1].Equals("days", StringComparison.OrdinalIgnoreCase))
                {
                    // Format: TableName:days:DayCount
                    string tableName = parts[0].Trim();
                    if (int.TryParse(parts[2].Trim(), out int dayCount))
                    {
                        result[tableName] = (CopyStrategyType.ModifiedDate, dayCount);
                    }
                }
            }

            return result;
        }

        private (CopyStrategyType, int) GetStrategy(string tableName, Dictionary<string, (CopyStrategyType, int)> overrides)
        {
            if (overrides.TryGetValue(tableName, out var strategy))
                return strategy;

            return (CopyStrategyType.RecId, _config.DefaultRecordCount);
        }

        private List<string> GetPatterns(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new List<string>();

            return input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
        }

        private bool MatchesAnyPattern(string tableName, List<string> patterns)
        {
            if (patterns.Count == 0)
                return false;

            foreach (var pattern in patterns)
            {
                if (MatchesPattern(tableName, pattern))
                    return true;
            }

            return false;
        }

        private bool MatchesPattern(string tableName, string pattern)
        {
            // Convert wildcard pattern to regex
            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(tableName, regexPattern, RegexOptions.IgnoreCase);
        }

        private Dictionary<string, List<string>> GetExcludedFieldsMap(string fieldsToExclude)
        {
            var result = new Dictionary<string, List<string>>();
            result[""] = new List<string>(); // Global exclusions

            if (string.IsNullOrWhiteSpace(fieldsToExclude))
                return result;

            var lines = fieldsToExclude.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                if (trimmed.Contains('.'))
                {
                    // Per-table exclusion: TableName.FieldName
                    var parts = trimmed.Split('.');
                    if (parts.Length == 2)
                    {
                        string tableName = parts[0].Trim().ToUpper();
                        string fieldName = parts[1].Trim().ToUpper();

                        if (!result.ContainsKey(tableName))
                            result[tableName] = new List<string>();

                        result[tableName].Add(fieldName);
                    }
                }
                else
                {
                    // Global exclusion
                    result[""].Add(trimmed.ToUpper());
                }
            }

            return result;
        }

        private string GenerateFetchSql(string tableName, List<string> fields, CopyStrategyType strategyType, int strategyValue)
        {
            string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));

            if (strategyType == CopyStrategyType.RecId)
            {
                return $"SELECT TOP ({strategyValue}) {fieldList} FROM [{tableName}] ORDER BY RecId DESC";
            }
            else
            {
                return $"SELECT {fieldList} FROM [{tableName}] WHERE [MODIFIEDDATETIME] > @CutoffDate";
            }
        }

        private void OnTablesUpdated()
        {
            TablesUpdated?.Invoke(this, _tables);
        }

        private void OnStatusUpdated(string status)
        {
            StatusUpdated?.Invoke(this, status);
        }
    }
}
