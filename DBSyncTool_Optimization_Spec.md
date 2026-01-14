# D365FO Database Sync Tool - SysRowVersion Optimization Specification

**Version:** 2.0  
**Date:** December 2024

---

## 1. Overview

### 1.1 Purpose

This document specifies optimizations to the D365FO Database Sync Tool to significantly reduce sync time by leveraging `SysRowVersion` (SQL Server timestamp) for incremental change detection, along with strategy simplification and estimation fixes.

### 1.2 Problem Statement

Current implementation has several issues:
- Always fetches full data from Tier2 and performs delete + insert operations (inefficient for small changes)
- Large tables (500k+ records) require full refresh even for minor changes
- Delete operations are slow (55% of total time for fully modified tables)
- EstimatedSizeMB calculation is incorrect for strategies without explicit record count
- Multiple overlapping strategies (days, where, all, RecIdWithWhere) are complex and can be unified

### 1.3 Solution Summary

1. **SysRowVersion Optimization**: Two-query approach with stored timestamps for incremental sync
2. **Strategy Simplification**: Remove days/where/all strategies, add flexible SQL strategy
3. **EstimatedSizeMB Fix**: Use DefaultRecordCount for estimation when explicit count not specified

---

## 2. New Configuration Fields

### 2.1 Threshold Settings (Connection Tab)

Add to "Execution Settings" section:

| Field | Type | Description | Default |
|-------|------|-------------|---------|
| Truncate Threshold % | NumericUpDown | If change percentage exceeds this, use TRUNCATE + bulk insert | 40 |

**UI Placement:** Below "Parallel Workers" in Connection tab.

### 2.2 Timestamp Storage (Connection Tab)

Add two new multiline text controls in Connection tab:

#### 2.2.1 Tier2 Timestamps

| Field | Type | Description |
|-------|------|-------------|
| Label | "Tier2 Timestamps" | |
| Control | TextBox (Multiline) | Stores last sync timestamps per table |
| Format | `TableName,0xHEXTIMESTAMP` (one per line) | |
| Editable | Yes | User can manually clear/edit |

**Example Content:**
```
CUSTTABLE,0x0000000000123456
SALESLINE,0x0000000000234567
INVENTTRANS,0x0000000000345678
```

#### 2.2.2 AxDB Timestamps

| Field | Type | Description |
|-------|------|-------------|
| Label | "AxDB Timestamps" | |
| Control | TextBox (Multiline) | Stores AxDB state after last sync |
| Format | `TableName,0xHEXTIMESTAMP` (one per line) | |
| Editable | Yes | User can manually clear/edit |

**Example Content:**
```
CUSTTABLE,0x0000000000AABBCC
SALESLINE,0x0000000000BBCCDD
INVENTTRANS,0x0000000000CCDDEE
```

**UI Notes:**
- Both controls should be 300x150 pixels approximately
- Place in a new GroupBox "Sync Timestamps" below "System Excluded Tables"
- Add "Clear All" button next to each control to reset timestamps
- Timestamps are stored in configuration file and persist across sessions

### 2.3 Updated AppConfiguration Model

```csharp
public class AppConfiguration
{
    // ... existing fields ...
    
    // New threshold setting
    public int TruncateThresholdPercent { get; set; } = 40;
    
    // New timestamp storage
    public string Tier2Timestamps { get; set; } = "";  // Multiline: TableName,0xTimestamp
    public string AxDBTimestamps { get; set; } = "";   // Multiline: TableName,0xTimestamp
}
```

---

## 3. EstimatedSizeMB Calculation Fix

### 3.1 Problem

Currently, `EstimatedSizeMB` is calculated using `RecordsToCopy`, but for strategies without explicit record count (like the old `days:30` or `where:` strategies), `RecordsToCopy` was set to `Tier2RowCount` (full table count), which overestimates the size.

### 3.2 Solution

For all strategies, use the effective record count for estimation:

```csharp
// Calculate records to copy based on strategy
long recordsToCopy = strategy.StrategyType switch
{
    CopyStrategyType.RecId => strategy.RecIdCount ?? _config.DefaultRecordCount,
    CopyStrategyType.Sql => strategy.RecIdCount ?? _config.DefaultRecordCount,
    _ => _config.DefaultRecordCount  // Fallback
};

// Calculate estimated size using minimum of RecordsToCopy and Tier2RowCount
long recordsForCalculation = Math.Min(recordsToCopy, table.Tier2RowCount);
decimal estimatedSizeMB = table.BytesPerRow > 0 && recordsForCalculation > 0
    ? (decimal)table.BytesPerRow * recordsForCalculation / 1_000_000m
    : 0;
```

### 3.3 After Fetch Update

After fetching data, update `EstimatedSizeMB` with actual fetched count (existing logic, keep as-is):

```csharp
// In ProcessSingleTableAsync, after fetch:
table.RecordsToCopy = data.Rows.Count;
long recordsForCalculation = Math.Min(data.Rows.Count, table.Tier2RowCount);
table.EstimatedSizeMB = table.BytesPerRow > 0 && recordsForCalculation > 0
    ? (decimal)table.BytesPerRow * recordsForCalculation / 1_000_000m
    : 0;
```

---

## 4. Strategy Simplification

### 4.1 Strategies to Remove

Remove the following strategies and all associated code:

| Strategy | Example | Replacement |
|----------|---------|-------------|
| `ModifiedDate` | `days:30` | Use SQL strategy |
| `Where` | `where:DATAAREAID='1000'` | Use SQL strategy |
| `RecIdWithWhere` | `5000\|where:DATAAREAID='1000'` | Use SQL strategy |
| `ModifiedDateWithWhere` | `days:30\|where:DATAAREAID='1000'` | Use SQL strategy |
| `All` | `all` | Use RecId with `-truncate` flag |

### 4.2 Remaining Strategies

Only two strategies remain:

| Strategy | Format | Description |
|----------|--------|-------------|
| `RecId` | `TableName\|5000` or `TableName` | Top N records by RecId (default strategy) |
| `Sql` | `TableName\|sql:SELECT...` | Custom SQL query |

### 4.3 Updated CopyStrategyType Enum

```csharp
public enum CopyStrategyType
{
    RecId,  // Top N by RecId (default)
    Sql     // Custom SQL query
}
```

### 4.4 SQL Strategy Specification

#### 4.4.1 Format

```
TableName|[RecordCount|]sql:SELECT_STATEMENT [-truncate]
```

**Components:**
- `TableName`: Required, the table to copy
- `RecordCount`: Optional, explicit record count (uses DefaultRecordCount if omitted)
- `sql:`: Required prefix for SQL strategy
- `SELECT_STATEMENT`: Required, must contain `*` and should contain `ORDER BY RecId DESC`
- `-truncate`: Optional flag to force TRUNCATE mode

#### 4.4.2 Parameter Replacement

The SQL statement supports these parameters:
- `@recordCount`: Replaced with explicit count or DefaultRecordCount
- `*`: Replaced based on query type:
  - **Control Query**: Replaced with `RecId, SysRowVersion`
  - **Data Query**: Replaced with all copyable fields (e.g., `[RECID], [ACCOUNTNUM], [DATAAREAID], ...`)

#### 4.4.3 Examples

**Basic usage with explicit count:**
```
CUSTTABLE|500|sql:SELECT TOP @recordCount * FROM CUSTTABLE ORDER BY RecId DESC
```

**Using global DefaultRecordCount:**
```
CUSTTABLE|sql:SELECT TOP @recordCount * FROM CUSTTABLE ORDER BY RecId DESC
```

**Force TRUNCATE mode:**
```
CUSTTABLE|500|sql:SELECT TOP @recordCount * FROM CUSTTABLE ORDER BY RecId DESC -truncate
```

**Filter by company (replaces old `where:` strategy):**
```
CUSTTABLE|sql:SELECT TOP @recordCount * FROM CUSTTABLE WHERE DATAAREAID='1000' ORDER BY RecId DESC
```

**Date filter (replaces old `days:` strategy):**
```
CUSTTABLE|sql:SELECT TOP @recordCount * FROM CUSTTABLE WHERE MODIFIEDDATETIME > DATEADD(day, -30, GETUTCDATE()) ORDER BY RecId DESC
```

**Complex join query:**
```
SALESLINE|sql:SELECT TOP @recordCount sl.* FROM SALESLINE sl INNER JOIN SALESTABLE st ON sl.SALESID = st.SALESID WHERE st.SALESSTATUS = 1 ORDER BY sl.RecId DESC
```

**Union query:**
```
CUSTTABLE|sql:SELECT TOP @recordCount * FROM (SELECT * FROM CUSTTABLE WHERE DATAAREAID='1000' UNION ALL SELECT * FROM CUSTTABLE WHERE DATAAREAID='2000') t ORDER BY RecId DESC
```

#### 4.4.4 Validation

**On Parse (in CopyOrchestrator.ParseStrategyLine):**
- SQL must contain `*` character
- Show warning MessageBox if SQL doesn't contain `ORDER BY RecId DESC` (but allow proceeding)

**Validation Code:**
```csharp
private StrategyOverride ParseSqlStrategy(string tableName, string sqlPart, int? recordCount, bool useTruncate)
{
    // Extract SQL after "sql:" prefix
    string sql = sqlPart.Substring(4).Trim();
    
    // Validate: must contain *
    if (!sql.Contains("*"))
    {
        throw new Exception("SQL strategy must contain '*' for field replacement");
    }
    
    // Warning: should contain ORDER BY RecId DESC (non-blocking)
    if (!sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) || 
        !sql.Contains("RecId", StringComparison.OrdinalIgnoreCase))
    {
        // Show warning but don't block
        // This will be handled in UI when user edits the strategy
    }
    
    return new StrategyOverride
    {
        TableName = tableName,
        StrategyType = CopyStrategyType.Sql,
        RecIdCount = recordCount,
        SqlTemplate = sql,
        UseTruncate = useTruncate
    };
}
```

**UI Warning (in MainForm):**
When user leaves the Strategy Overrides text field, validate and show warning:

```csharp
private void TxtStrategyOverrides_Leave(object sender, EventArgs e)
{
    var lines = txtStrategyOverrides.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    var warnings = new List<string>();
    
    foreach (var line in lines)
    {
        if (line.Contains("sql:", StringComparison.OrdinalIgnoreCase))
        {
            // Check for ORDER BY RecId DESC
            if (!line.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) ||
                !line.Contains("RecId", StringComparison.OrdinalIgnoreCase) ||
                !line.Contains("DESC", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Line '{line.Substring(0, Math.Min(50, line.Length))}...' - SQL should contain 'ORDER BY RecId DESC'");
            }
            
            // Check for *
            if (!line.Contains("*"))
            {
                warnings.Add($"Line '{line.Substring(0, Math.Min(50, line.Length))}...' - SQL must contain '*' for field replacement");
            }
        }
    }
    
    if (warnings.Count > 0)
    {
        MessageBox.Show(
            "SQL Strategy Warnings:\n\n" + string.Join("\n", warnings) + 
            "\n\nSQL strategies should contain '*' (required) and 'ORDER BY RecId DESC' (recommended).",
            "Strategy Validation Warning",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }
}
```

#### 4.4.5 SQL Generation

**Control Query (for optimized mode):**
```csharp
public string GenerateControlSql(string sqlTemplate, int recordCount)
{
    // Replace @recordCount parameter
    string sql = sqlTemplate.Replace("@recordCount", recordCount.ToString());
    
    // Replace * with RecId, SysRowVersion
    sql = sql.Replace("*", "RecId, SysRowVersion");
    
    return sql;
}
```

**Data Query:**
```csharp
public string GenerateDataSql(string sqlTemplate, int recordCount, List<string> copyableFields)
{
    // Replace @recordCount parameter
    string sql = sqlTemplate.Replace("@recordCount", recordCount.ToString());
    
    // Replace * with field list
    string fieldList = string.Join(", ", copyableFields.Select(f => $"[{f}]"));
    sql = sql.Replace("*", fieldList);
    
    return sql;
}
```

**Data Query with Timestamp Filter (for optimized incremental mode):**
```csharp
public string GenerateDataSqlWithTimestamp(string sqlTemplate, int recordCount, List<string> copyableFields, long minRecId)
{
    // Replace @recordCount parameter
    string sql = sqlTemplate.Replace("@recordCount", recordCount.ToString());
    
    // Replace * with field list
    string fieldList = string.Join(", ", copyableFields.Select(f => $"[{f}]"));
    sql = sql.Replace("*", fieldList);
    
    // Add SysRowVersion and RecId filters
    // This is complex for arbitrary SQL - need to inject WHERE/AND clauses
    // For simplicity, wrap in subquery:
    sql = $@"
        SELECT {fieldList} FROM (
            {sql}
        ) AS SourceData
        WHERE SysRowVersion > @FetchThreshold 
          AND RecId >= @MinRecId";
    
    return sql;
}
```

### 4.5 Updated StrategyOverride Class

```csharp
public class StrategyOverride
{
    public string TableName { get; set; } = string.Empty;
    public CopyStrategyType StrategyType { get; set; }
    public int? RecIdCount { get; set; }      // For RecId strategy or SQL with explicit count
    public string SqlTemplate { get; set; } = string.Empty;  // For SQL strategy (raw template with * and @recordCount)
    public bool UseTruncate { get; set; }
}
```

### 4.6 Updated TableInfo Model

```csharp
public class TableInfo
{
    // ... existing fields ...
    
    // Updated strategy fields
    public CopyStrategyType StrategyType { get; set; }
    public int? RecIdCount { get; set; }       // Explicit count or null for default
    public string SqlTemplate { get; set; } = string.Empty;  // For SQL strategy
    public bool UseTruncate { get; set; }
    
    // Remove these fields:
    // public int? DaysCount { get; set; }     // REMOVED
    // public string WhereClause { get; set; } // REMOVED
    
    // Updated display property
    public string StrategyDisplay
    {
        get
        {
            var parts = new List<string>();
            
            switch (StrategyType)
            {
                case CopyStrategyType.RecId:
                    parts.Add($"RecId:{RecIdCount ?? 0}");
                    break;
                case CopyStrategyType.Sql:
                    parts.Add($"SQL:{RecIdCount ?? 0}");
                    break;
            }
            
            if (UseTruncate)
                parts.Add("TRUNC");
            
            return string.Join(" ", parts);
        }
    }
}
```

### 4.7 Updated Strategy Parsing

```csharp
private StrategyOverride ParseStrategyLine(string line)
{
    // Check for -truncate flag at the end
    bool useTruncate = false;
    string workingLine = line;
    
    if (line.EndsWith(" -truncate", StringComparison.OrdinalIgnoreCase))
    {
        useTruncate = true;
        workingLine = line.Substring(0, line.Length - 10).Trim();
    }
    
    // Split by pipe
    var parts = workingLine.Split('|');
    
    if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        throw new Exception("Invalid format: missing table name");
    
    string tableName = parts[0].Trim();
    
    // Case 1: TableName only (use default RecId strategy)
    if (parts.Length == 1)
    {
        return new StrategyOverride
        {
            TableName = tableName,
            StrategyType = CopyStrategyType.RecId,
            RecIdCount = _config.DefaultRecordCount,
            UseTruncate = useTruncate
        };
    }
    
    string part1 = parts[1].Trim();
    
    // Case 2: TableName|sql:... (SQL without explicit count)
    if (part1.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
    {
        return ParseSqlStrategy(tableName, part1, null, useTruncate);
    }
    
    // Case 3: TableName|Number (RecId strategy)
    if (int.TryParse(part1, out int count))
    {
        if (count <= 0)
            throw new Exception("Invalid format: RecId count must be positive");
        
        // Check if there's a sql: part after the count
        if (parts.Length >= 3)
        {
            string part2 = parts[2].Trim();
            if (part2.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
            {
                // Case 4: TableName|Number|sql:... (SQL with explicit count)
                return ParseSqlStrategy(tableName, part2, count, useTruncate);
            }
            else
            {
                throw new Exception($"Invalid format: unexpected '{part2}' after record count");
            }
        }
        
        return new StrategyOverride
        {
            TableName = tableName,
            StrategyType = CopyStrategyType.RecId,
            RecIdCount = count,
            UseTruncate = useTruncate
        };
    }
    
    throw new Exception($"Invalid format: '{part1}' is not a valid strategy (expected number or 'sql:...')");
}

private StrategyOverride ParseSqlStrategy(string tableName, string sqlPart, int? recordCount, bool useTruncate)
{
    // Extract SQL after "sql:" prefix
    string sql = sqlPart.Substring(4).Trim();
    
    if (string.IsNullOrEmpty(sql))
        throw new Exception("Invalid format: empty SQL statement");
    
    // Validate: must contain *
    if (!sql.Contains("*"))
        throw new Exception("SQL strategy must contain '*' for field replacement");
    
    return new StrategyOverride
    {
        TableName = tableName,
        StrategyType = CopyStrategyType.Sql,
        RecIdCount = recordCount,
        SqlTemplate = sql,
        UseTruncate = useTruncate
    };
}
```

### 4.8 Updated Fetch SQL Generation

```csharp
private string GenerateFetchSql(string tableName, List<string> fields, StrategyOverride strategy)
{
    string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));
    int recordCount = strategy.RecIdCount ?? _config.DefaultRecordCount;
    
    switch (strategy.StrategyType)
    {
        case CopyStrategyType.RecId:
            return $"SELECT TOP ({recordCount}) {fieldList} FROM [{tableName}] ORDER BY RecId DESC";
        
        case CopyStrategyType.Sql:
            // Replace parameters in SQL template
            string sql = strategy.SqlTemplate
                .Replace("@recordCount", recordCount.ToString())
                .Replace("*", fieldList);
            return sql;
        
        default:
            throw new Exception($"Unsupported strategy type: {strategy.StrategyType}");
    }
}

private string GenerateControlSql(string tableName, StrategyOverride strategy)
{
    int recordCount = strategy.RecIdCount ?? _config.DefaultRecordCount;
    
    switch (strategy.StrategyType)
    {
        case CopyStrategyType.RecId:
            return $"SELECT TOP ({recordCount}) RecId, SysRowVersion FROM [{tableName}] ORDER BY RecId DESC";
        
        case CopyStrategyType.Sql:
            // Replace parameters in SQL template
            string sql = strategy.SqlTemplate
                .Replace("@recordCount", recordCount.ToString())
                .Replace("*", "RecId, SysRowVersion");
            return sql;
        
        default:
            throw new Exception($"Unsupported strategy type: {strategy.StrategyType}");
    }
}
```

### 4.9 Code Removal Checklist

Remove from `CopyOrchestrator.cs`:
- [ ] `CopyStrategyType.ModifiedDate`
- [ ] `CopyStrategyType.Where`
- [ ] `CopyStrategyType.RecIdWithWhere`
- [ ] `CopyStrategyType.ModifiedDateWithWhere`
- [ ] `CopyStrategyType.All`
- [ ] All parsing logic for `days:`, `where:`, `all`
- [ ] `DaysCount` property handling
- [ ] `WhereClause` property handling

Remove from `TableInfo.cs`:
- [ ] `DaysCount` property
- [ ] `WhereClause` property
- [ ] Related `StrategyDisplay` cases

Remove from `AxDbDataService.cs`:
- [ ] `DeleteByModifiedDateAsync` method
- [ ] `DeleteByWhereClauseAsync` method
- [ ] Related cleanup logic cases

Remove from `Tier2DataService.cs`:
- [ ] `FetchDataByModifiedDateAsync` method

Update `MainForm.cs`:
- [ ] Update tooltip for Strategy Overrides label
- [ ] Add validation warning on text field leave
- [ ] Update `GenerateCleanupSql` and `GetCleanupDescription` methods

---

## 5. SysRowVersion Optimization Algorithm

### 9.1 Decision Tree

```
For each table:
├── Has SysRowVersion column?
│   ├── YES: Has stored timestamps (both Tier2 and AxDB)?
│   │   ├── YES: Use OPTIMIZED MODE (this spec)
│   │   └── NO: TRUNCATE + full insert (first sync stores timestamps)
│   └── NO: TRUNCATE + full insert
```

### 9.2 Optimized Mode Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                    OPTIMIZED MODE                                │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ STEP 1: Control Query from Tier2                         │   │
│  │ SELECT TOP X RecId, SysRowVersion ORDER BY RecId DESC    │   │
│  └──────────────────────────────────────────────────────────┘   │
│                            │                                     │
│                            ▼                                     │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ STEP 2: Evaluate Change Volume                           │   │
│  │ Count: Tier2 changes + AxDB changes + AxDB excess rows   │   │
│  │ If > Threshold% → TRUNCATE MODE                          │   │
│  └──────────────────────────────────────────────────────────┘   │
│                            │                                     │
│              ┌─────────────┴─────────────┐                      │
│              ▼                           ▼                      │
│  ┌─────────────────────┐    ┌─────────────────────────────┐    │
│  │   TRUNCATE MODE     │    │     INCREMENTAL MODE        │    │
│  │                     │    │                             │    │
│  │ 1. Fetch full data  │    │ 1. Delete modified (Tier2)  │    │
│  │ 2. TRUNCATE table   │    │ 2. Delete modified (AxDB)   │    │
│  │ 3. Bulk insert all  │    │ 3. Delete not in Tier2      │    │
│  │                     │    │ 4. Fetch changed records    │    │
│  │                     │    │ 5. Filter & bulk insert     │    │
│  └─────────────────────┘    └─────────────────────────────┘    │
│                            │                                     │
│                            ▼                                     │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ STEP FINAL: Update stored timestamps (on success only)   │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 6. Detailed SysRowVersion Algorithm Specification

### 8.1 Prerequisites Check

Before executing optimized mode, verify:

```csharp
bool CanUseOptimizedMode(TableInfo table)
{
    // 1. Table must have SysRowVersion in copyable fields
    bool hasSysRowVersion = table.CopyableFields
        .Any(f => f.Equals("SYSROWVERSION", StringComparison.OrdinalIgnoreCase));
    
    // 2. Both timestamps must exist for this table
    bool hasTier2Timestamp = GetStoredTier2Timestamp(table.TableName) != null;
    bool hasAxDBTimestamp = GetStoredAxDBTimestamp(table.TableName) != null;
    
    return hasSysRowVersion && hasTier2Timestamp && hasAxDBTimestamp;
}
```

**Note:** SysRowVersion should NOT be in "Fields to Exclude" - it should be removed from default exclusions and handled specially (selected for comparison but not copied).

### 8.2 Step 1: Control Query from Tier2

```sql
-- Tier2ControlQuery
SELECT TOP (@RecordCount) RecId, SysRowVersion 
FROM [@TableName] 
ORDER BY RecId DESC
```

**C# Implementation:**
```csharp
public async Task<DataTable> FetchControlDataAsync(string tableName, int recordCount, CancellationToken ct)
{
    string sql = $@"
        SELECT TOP ({recordCount}) RecId, SysRowVersion 
        FROM [{tableName}] 
        ORDER BY RecId DESC";
    
    // Execute and return DataTable with RecId, SysRowVersion columns
}
```

**Calculate from results:**
- `@MinRecIdFromTier2` = MIN(RecId)
- `@MaxSysRowVersionFromTier2` = MAX(SysRowVersion)
- `@TotalRows` = Row count

### 8.3 Step 2: Evaluate Change Volume

```csharp
public async Task<ChangeVolumeResult> EvaluateChangeVolumeAsync(
    DataTable tier2Control, 
    byte[] storedTier2Timestamp,
    byte[] storedAxDBTimestamp,
    string tableName,
    int truncateThreshold)
{
    long totalRows = tier2Control.Rows.Count;
    
    // Count Tier2 changes
    long tier2ChangedCount = tier2Control.AsEnumerable()
        .Count(row => CompareTimestamp(row.Field<byte[]>("SysRowVersion"), storedTier2Timestamp) > 0);
    
    // Count AxDB changes
    string axdbChangedSql = @"
        SELECT COUNT(*) FROM [@TableName] 
        WHERE SysRowVersion > @StoredAxDBTimestamp";
    long axdbChangedCount = await ExecuteScalarAsync(axdbChangedSql, storedAxDBTimestamp);
    
    // Count AxDB total rows (to detect excess)
    string axdbTotalSql = "SELECT COUNT(*) FROM [@TableName]";
    long axdbTotalCount = await ExecuteScalarAsync(axdbTotalSql);
    
    // Calculate percentages
    long totalChanges = tier2ChangedCount + axdbChangedCount;
    double changePercent = (double)totalChanges / totalRows * 100;
    
    // Check if AxDB has too many excess rows (more than 40% over transfer size)
    double excessPercent = (double)(axdbTotalCount - totalRows) / totalRows * 100;
    bool hasExcessRows = excessPercent > truncateThreshold;
    
    return new ChangeVolumeResult
    {
        Tier2ChangedCount = tier2ChangedCount,
        AxDBChangedCount = axdbChangedCount,
        AxDBTotalCount = axdbTotalCount,
        ChangePercent = changePercent,
        ExcessPercent = excessPercent,
        UseTruncate = changePercent > truncateThreshold || hasExcessRows
    };
}
```

**Decision Logic:**
```
IF (Tier2ChangedCount + AxDBChangedCount) > TruncateThreshold% of TotalRows
   OR AxDBTotalCount > (TotalRows * (1 + TruncateThreshold%))
THEN
   Use TRUNCATE MODE
ELSE
   Use INCREMENTAL MODE
```

### 8.4 TRUNCATE MODE

When change volume exceeds threshold:

```csharp
public async Task ExecuteTruncateModeAsync(TableInfo table, CancellationToken ct)
{
    // 1. Fetch full data from Tier2
    string fetchSql = $@"
        SELECT TOP ({table.RecordsToCopy}) {GetFieldList(table.CopyableFields)} 
        FROM [{table.TableName}] 
        ORDER BY RecId DESC";
    
    DataTable fullData = await _tier2Service.FetchDataBySqlAsync(fetchSql, ct);
    
    // 2. TRUNCATE and insert (existing logic)
    // - Disable triggers
    // - TRUNCATE TABLE
    // - Bulk insert
    // - Enable triggers
    // - Update sequence
    
    // 3. Update timestamps
    await UpdateStoredTimestampsAsync(table.TableName, tier2MaxTimestamp, axdbMaxTimestamp);
}
```

### 6.5 INCREMENTAL MODE

When change volume is below threshold:

#### 6.5.1 Delete Operations

```csharp
public async Task ExecuteIncrementalDeletesAsync(
    TableInfo table,
    DataTable tier2Control,
    byte[] storedTier2Timestamp,
    byte[] storedAxDBTimestamp,
    SqlConnection connection,
    SqlTransaction transaction,
    CancellationToken ct)
{
    // Create temp table with Tier2 RecIds for efficient joins
    await CreateTempTableAsync(tier2Control, connection, transaction);
    
    // 1.1: Delete records modified in Tier2
    string delete1 = $@"
        DELETE FROM [{table.TableName}] 
        WHERE RecId IN (
            SELECT RecId FROM #Tier2Control 
            WHERE SysRowVersion > @Tier2Timestamp
        )";
    await ExecuteDeleteAsync(delete1, storedTier2Timestamp, connection, transaction);
    
    // 1.2: Delete records modified in AxDB
    string delete2 = $@"
        DELETE FROM [{table.TableName}] 
        WHERE SysRowVersion > @AxDBTimestamp";
    await ExecuteDeleteAsync(delete2, storedAxDBTimestamp, connection, transaction);
    
    // 1.3: Delete records not in Tier2 target set
    string delete3 = $@"
        DELETE FROM [{table.TableName}] 
        WHERE NOT EXISTS (
            SELECT 1 FROM #Tier2Control t 
            WHERE t.RecId = [{table.TableName}].RecId
        )";
    await ExecuteDeleteAsync(delete3, connection, transaction);
    
    // Drop temp table
    await DropTempTableAsync(connection, transaction);
}

private async Task CreateTempTableAsync(DataTable tier2Control, SqlConnection conn, SqlTransaction trans)
{
    // Create temp table
    string createSql = @"
        CREATE TABLE #Tier2Control (
            RecId BIGINT PRIMARY KEY,
            SysRowVersion BINARY(8)
        )";
    await ExecuteNonQueryAsync(createSql, conn, trans);
    
    // Bulk insert Tier2 control data into temp table
    using var bulkCopy = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, trans);
    bulkCopy.DestinationTableName = "#Tier2Control";
    bulkCopy.ColumnMappings.Add("RecId", "RecId");
    bulkCopy.ColumnMappings.Add("SysRowVersion", "SysRowVersion");
    await bulkCopy.WriteToServerAsync(tier2Control);
}
```

#### 6.5.2 Fetch and Insert Operations

```csharp
public async Task ExecuteIncrementalInsertAsync(
    TableInfo table,
    DataTable tier2Control,
    byte[] storedTier2Timestamp,
    SqlConnection connection,
    SqlTransaction transaction,
    CancellationToken ct)
{
    // 2.0: Get remaining RecIds from AxDB (after deletes)
    string getAxDBRecIdsSql = $"SELECT RecId FROM [{table.TableName}]";
    HashSet<long> existingRecIds = await GetRecIdSetAsync(getAxDBRecIdsSql, connection, transaction);
    
    // Find missing RecIds (in Tier2Control but not in AxDB)
    var tier2RecIds = tier2Control.AsEnumerable()
        .Select(r => r.Field<long>("RecId"))
        .ToList();
    
    var missingRecIds = tier2RecIds.Where(id => !existingRecIds.Contains(id)).ToList();
    
    if (missingRecIds.Count == 0)
    {
        // Nothing to insert
        return;
    }
    
    // 2.1: Calculate fetch threshold
    byte[] minMissingSysRowVersion = tier2Control.AsEnumerable()
        .Where(r => missingRecIds.Contains(r.Field<long>("RecId")))
        .Select(r => r.Field<byte[]>("SysRowVersion"))
        .Min(new TimestampComparer());
    
    byte[] fetchThreshold = MinTimestamp(minMissingSysRowVersion, storedTier2Timestamp);
    long minRecId = tier2Control.AsEnumerable().Min(r => r.Field<long>("RecId"));
    
    // 2.2: Fetch changed/new records from Tier2
    string fetchSql = $@"
        SELECT TOP ({table.RecordsToCopy}) {GetFieldList(table.CopyableFields)} 
        FROM [{table.TableName}] 
        WHERE SysRowVersion > @FetchThreshold 
          AND RecId >= @MinRecId
        ORDER BY RecId DESC";
    
    DataTable tier2Data = await _tier2Service.FetchDataWithParamsAsync(
        fetchSql, 
        fetchThreshold, 
        minRecId, 
        ct);
    
    // 2.3: Filter in memory - keep only records not in AxDB
    var rowsToInsert = tier2Data.AsEnumerable()
        .Where(r => !existingRecIds.Contains(r.Field<long>("RecId")))
        .CopyToDataTable();
    
    // 2.4: Bulk insert
    if (rowsToInsert.Rows.Count > 0)
    {
        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction);
        bulkCopy.DestinationTableName = table.TableName;
        bulkCopy.BatchSize = 10000;
        
        foreach (DataColumn col in rowsToInsert.Columns)
        {
            bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        }
        
        await bulkCopy.WriteToServerAsync(rowsToInsert, ct);
    }
}
```

### 6.6 Update Stored Timestamps

Only called on successful completion:

```csharp
public async Task UpdateStoredTimestampsAsync(
    string tableName,
    byte[] tier2MaxTimestamp,
    SqlConnection axdbConnection)
{
    // Get AxDB max timestamp after insert
    string sql = $"SELECT MAX(SysRowVersion) FROM [{tableName}]";
    byte[] axdbMaxTimestamp = await ExecuteScalarBinaryAsync(sql, axdbConnection);
    
    // Update configuration
    UpdateTier2Timestamp(tableName, tier2MaxTimestamp);
    UpdateAxDBTimestamp(tableName, axdbMaxTimestamp);
    
    // Save configuration to file
    _configManager.SaveConfiguration(_currentConfig);
}
```

---

## 7. Helper Classes and Methods

### 9.1 Timestamp Comparison

```csharp
public static class TimestampHelper
{
    /// <summary>
    /// Compares two SQL Server timestamps (binary(8))
    /// Returns: -1 if a < b, 0 if equal, 1 if a > b
    /// </summary>
    public static int CompareTimestamp(byte[] a, byte[] b)
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
    public static byte[] MinTimestamp(byte[] a, byte[] b)
    {
        return CompareTimestamp(a, b) <= 0 ? a : b;
    }
    
    /// <summary>
    /// Converts timestamp to hex string for storage
    /// </summary>
    public static string ToHexString(byte[] timestamp)
    {
        if (timestamp == null) return "";
        return "0x" + BitConverter.ToString(timestamp).Replace("-", "");
    }
    
    /// <summary>
    /// Parses hex string back to timestamp
    /// </summary>
    public static byte[]? FromHexString(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        
        hex = hex.TrimStart('0', 'x', 'X');
        if (hex.Length != 16) return null;
        
        byte[] bytes = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}
```

### 9.2 Timestamp Storage Manager

```csharp
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
```

---

## 8. Changes to Existing Classes

### 8.1 TableInfo Model Updates

```csharp
public class TableInfo
{
    // ... existing fields ...
    
    // New fields for optimized mode
    public bool UseOptimizedMode { get; set; }
    public byte[]? StoredTier2Timestamp { get; set; }
    public byte[]? StoredAxDBTimestamp { get; set; }
    public DataTable? ControlData { get; set; }  // RecId, SysRowVersion from Tier2
    
    // Execution metrics
    public long Tier2ChangedCount { get; set; }
    public long AxDBChangedCount { get; set; }
    public double ChangePercent { get; set; }
    public bool UsedTruncate { get; set; }
}
```

### 8.2 CopyOrchestrator Updates

**In PrepareTableListAsync:**
```csharp
// After determining copyable fields, check for optimized mode
bool hasSysRowVersion = copyableFields
    .Any(f => f.Equals("SYSROWVERSION", StringComparison.OrdinalIgnoreCase));

byte[]? tier2Ts = _timestampManager.GetTier2Timestamp(tableName);
byte[]? axdbTs = _timestampManager.GetAxDBTimestamp(tableName);

bool useOptimizedMode = hasSysRowVersion && tier2Ts != null && axdbTs != null;

var tableInfo = new TableInfo
{
    // ... existing properties ...
    UseOptimizedMode = useOptimizedMode,
    StoredTier2Timestamp = tier2Ts,
    StoredAxDBTimestamp = axdbTs
};
```

**In ProcessSingleTableAsync:**
```csharp
if (table.UseOptimizedMode)
{
    await ProcessTableOptimizedAsync(table, cancellationToken);
}
else
{
    await ProcessTableStandardAsync(table, cancellationToken);  // Existing logic
}
```

### 8.3 Tier2DataService Updates

Add new method:
```csharp
/// <summary>
/// Fetches control data (RecId, SysRowVersion) for optimized comparison
/// </summary>
public async Task<DataTable> FetchControlDataAsync(
    string tableName, 
    int recordCount, 
    CancellationToken cancellationToken)
{
    string sql = $@"
        SELECT TOP ({recordCount}) RecId, SysRowVersion 
        FROM [{tableName}] 
        ORDER BY RecId DESC";
    
    _logger($"[Tier2 SQL] Control query: {sql}");
    
    using var connection = new SqlConnection(_connectionString);
    using var command = new SqlCommand(sql, connection);
    command.CommandTimeout = _connectionSettings.CommandTimeout;
    
    var dataTable = new DataTable();
    await connection.OpenAsync(cancellationToken);
    
    using var adapter = new SqlDataAdapter(command);
    adapter.Fill(dataTable);
    
    return dataTable;
}

/// <summary>
/// Fetches data with SysRowVersion filter
/// </summary>
public async Task<DataTable> FetchDataByTimestampAsync(
    string tableName,
    List<string> fields,
    int recordCount,
    byte[] timestampThreshold,
    long minRecId,
    CancellationToken cancellationToken)
{
    string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));
    string sql = $@"
        SELECT TOP ({recordCount}) {fieldList} 
        FROM [{tableName}] 
        WHERE SysRowVersion > @Threshold 
          AND RecId >= @MinRecId
        ORDER BY RecId DESC";
    
    _logger($"[Tier2 SQL] Timestamp query: {sql}");
    
    using var connection = new SqlConnection(_connectionString);
    using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@Threshold", SqlDbType.Binary, 8).Value = timestampThreshold;
    command.Parameters.AddWithValue("@MinRecId", minRecId);
    command.CommandTimeout = _connectionSettings.CommandTimeout;
    
    var dataTable = new DataTable();
    await connection.OpenAsync(cancellationToken);
    
    using var adapter = new SqlDataAdapter(command);
    adapter.Fill(dataTable);
    
    return dataTable;
}
```

### 8.4 AxDbDataService Updates

Add new methods:
```csharp
/// <summary>
/// Gets count of changed records in AxDB
/// </summary>
public async Task<long> GetChangedCountAsync(
    string tableName, 
    byte[] timestampThreshold,
    CancellationToken cancellationToken)
{
    string sql = $"SELECT COUNT(*) FROM [{tableName}] WHERE SysRowVersion > @Threshold";
    
    using var connection = new SqlConnection(_connectionString);
    using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@Threshold", SqlDbType.Binary, 8).Value = timestampThreshold;
    command.CommandTimeout = _connectionSettings.CommandTimeout;
    
    await connection.OpenAsync(cancellationToken);
    return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
}

/// <summary>
/// Gets total row count in AxDB table
/// </summary>
public async Task<long> GetRowCountAsync(string tableName, CancellationToken cancellationToken)
{
    string sql = $"SELECT COUNT(*) FROM [{tableName}]";
    
    using var connection = new SqlConnection(_connectionString);
    using var command = new SqlCommand(sql, connection);
    command.CommandTimeout = _connectionSettings.CommandTimeout;
    
    await connection.OpenAsync(cancellationToken);
    return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
}

/// <summary>
/// Gets all RecIds from AxDB table
/// </summary>
public async Task<HashSet<long>> GetRecIdSetAsync(
    string tableName,
    SqlConnection connection,
    SqlTransaction transaction,
    CancellationToken cancellationToken)
{
    string sql = $"SELECT RecId FROM [{tableName}]";
    
    using var command = new SqlCommand(sql, connection, transaction);
    command.CommandTimeout = _connectionSettings.CommandTimeout;
    
    var result = new HashSet<long>();
    using var reader = await command.ExecuteReaderAsync(cancellationToken);
    
    while (await reader.ReadAsync(cancellationToken))
    {
        result.Add(reader.GetInt64(0));
    }
    
    return result;
}

/// <summary>
/// Gets max SysRowVersion from AxDB table
/// </summary>
public async Task<byte[]?> GetMaxTimestampAsync(
    string tableName,
    SqlConnection connection,
    SqlTransaction transaction)
{
    string sql = $"SELECT MAX(SysRowVersion) FROM [{tableName}]";
    
    using var command = new SqlCommand(sql, connection, transaction);
    command.CommandTimeout = _connectionSettings.CommandTimeout;
    
    var result = await command.ExecuteScalarAsync();
    return result as byte[];
}

/// <summary>
/// Executes optimized incremental delete operations
/// </summary>
public async Task ExecuteIncrementalDeletesAsync(
    TableInfo table,
    DataTable tier2Control,
    byte[] tier2Timestamp,
    byte[] axdbTimestamp,
    SqlConnection connection,
    SqlTransaction transaction,
    CancellationToken cancellationToken)
{
    var deleteStopwatch = Stopwatch.StartNew();
    
    // Create temp table with Tier2 RecIds
    _logger($"[AxDB] Creating temp table for {table.TableName}");
    await CreateTier2ControlTempTableAsync(tier2Control, connection, transaction, cancellationToken);
    
    // 1.1: Delete records modified in Tier2
    string delete1 = $@"
        DELETE FROM [{table.TableName}] 
        WHERE RecId IN (
            SELECT RecId FROM #Tier2Control 
            WHERE SysRowVersion > @Tier2Timestamp
        )";
    _logger($"[AxDB SQL] Delete Tier2-modified: {delete1}");
    
    using (var cmd = new SqlCommand(delete1, connection, transaction))
    {
        cmd.Parameters.Add("@Tier2Timestamp", SqlDbType.Binary, 8).Value = tier2Timestamp;
        cmd.CommandTimeout = _connectionSettings.CommandTimeout;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    
    // 1.2: Delete records modified in AxDB
    string delete2 = $@"
        DELETE FROM [{table.TableName}] 
        WHERE SysRowVersion > @AxDBTimestamp";
    _logger($"[AxDB SQL] Delete AxDB-modified: {delete2}");
    
    using (var cmd = new SqlCommand(delete2, connection, transaction))
    {
        cmd.Parameters.Add("@AxDBTimestamp", SqlDbType.Binary, 8).Value = axdbTimestamp;
        cmd.CommandTimeout = _connectionSettings.CommandTimeout;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    
    // 1.3: Delete records not in Tier2 target set
    string delete3 = $@"
        DELETE FROM [{table.TableName}] 
        WHERE NOT EXISTS (
            SELECT 1 FROM #Tier2Control t 
            WHERE t.RecId = [{table.TableName}].RecId
        )";
    _logger($"[AxDB SQL] Delete not-in-Tier2: {delete3}");
    
    using (var cmd = new SqlCommand(delete3, connection, transaction))
    {
        cmd.CommandTimeout = _connectionSettings.CommandTimeout;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    
    // Drop temp table
    using (var cmd = new SqlCommand("DROP TABLE #Tier2Control", connection, transaction))
    {
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    
    deleteStopwatch.Stop();
    table.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;
}

private async Task CreateTier2ControlTempTableAsync(
    DataTable tier2Control,
    SqlConnection connection,
    SqlTransaction transaction,
    CancellationToken cancellationToken)
{
    // Create temp table
    string createSql = @"
        CREATE TABLE #Tier2Control (
            RecId BIGINT PRIMARY KEY,
            SysRowVersion BINARY(8)
        )";
    
    using (var cmd = new SqlCommand(createSql, connection, transaction))
    {
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    
    // Bulk insert Tier2 control data
    using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
    bulkCopy.DestinationTableName = "#Tier2Control";
    bulkCopy.ColumnMappings.Add("RecId", "RecId");
    bulkCopy.ColumnMappings.Add("SysRowVersion", "SysRowVersion");
    bulkCopy.BatchSize = 10000;
    
    await bulkCopy.WriteToServerAsync(tier2Control, cancellationToken);
}
```

---

## 9. UI Changes

### 9.1 Connection Tab Layout Update

Add new GroupBox below "System Excluded Tables":

```
┌─ Sync Timestamps ────────────────────────────────────────────────────────┐
│                                                                           │
│  Truncate Threshold %: [40    ]                                          │
│                                                                           │
│  ┌─ Tier2 Timestamps ─────────────────┐  ┌─ AxDB Timestamps ─────────────┐│
│  │ CUSTTABLE,0x0000000000123456       │  │ CUSTTABLE,0x0000000000AABBCC  ││
│  │ SALESLINE,0x0000000000234567       │  │ SALESLINE,0x0000000000BBCCDD  ││
│  │ INVENTTRANS,0x0000000000345678     │  │ INVENTTRANS,0x0000000000CCDDEE││
│  │                                     │  │                               ││
│  │                                     │  │                               ││
│  └─────────────────────────────────────┘  └───────────────────────────────┘│
│  [Clear Tier2]                            [Clear AxDB]     [Clear All]    │
│                                                                           │
└───────────────────────────────────────────────────────────────────────────┘
```

### 9.2 MainForm.Designer.cs Additions

```csharp
// New controls for Sync Timestamps
private GroupBox grpSyncTimestamps;
private Label lblTruncateThreshold;
private NumericUpDown nudTruncateThreshold;
private Label lblTier2Timestamps;
private TextBox txtTier2Timestamps;
private Label lblAxDBTimestamps;
private TextBox txtAxDBTimestamps;
private Button btnClearTier2Timestamps;
private Button btnClearAxDBTimestamps;
private Button btnClearAllTimestamps;
```

### 9.3 DataGrid Updates

Add new columns to show optimization status:

| Column | Description |
|--------|-------------|
| Mode | "Optimized" or "Standard" |
| Change % | Percentage of changed records (for optimized mode) |

---

## 10. Handling Tables Without SysRowVersion

For tables without SysRowVersion or without stored timestamps, always use TRUNCATE + full insert:

```csharp
// In CopyOrchestrator.ProcessTableStandardAsync
if (!table.UseOptimizedMode)
{
    // No SysRowVersion or no stored timestamps - always TRUNCATE + full insert
    _logger($"[{table.TableName}] No optimization available - using TRUNCATE + full insert");
    await ProcessTableTruncateOnlyAsync(table, cancellationToken);
}
```

**ProcessTableTruncateOnlyAsync implementation:**
```csharp
private async Task ProcessTableTruncateOnlyAsync(TableInfo table, CancellationToken ct)
{
    // 1. Fetch full data from Tier2
    DataTable data = await _tier2Service.FetchDataBySqlAsync(
        table.TableName,
        table.FetchSql,
        null,  // No days parameter
        ct);
    
    table.CachedData = data;
    table.RecordsFetched = data.Rows.Count;
    
    // 2. TRUNCATE and insert
    await _axDbService.InsertDataWithTruncateAsync(table, ct);
    
    // 3. Update timestamps if table has SysRowVersion (for future syncs)
    if (table.CopyableFields.Any(f => f.Equals("SYSROWVERSION", StringComparison.OrdinalIgnoreCase)))
    {
        // Store timestamps for next sync to enable optimized mode
        byte[] tier2MaxTimestamp = GetMaxSysRowVersion(data);
        if (tier2MaxTimestamp != null)
        {
            await UpdateStoredTimestampsAsync(table.TableName, tier2MaxTimestamp);
        }
    }
}
```

---

## 11. Logging Updates

Add detailed logging for optimized mode:

```csharp
_logger($"[{table.TableName}] Optimized mode: Tier2 changes={tier2ChangedCount}, AxDB changes={axdbChangedCount}, Total={changePercent:F1}%");

if (useTruncate)
{
    _logger($"[{table.TableName}] Using TRUNCATE mode (threshold exceeded or excess rows)");
}
else
{
    _logger($"[{table.TableName}] Using INCREMENTAL mode");
}
```

---

## 12. Error Handling

### 12.1 Timestamp Update on Failure

If any operation fails:
- DO NOT update stored timestamps
- Transaction rollback ensures AxDB consistency
- Next sync will retry with same timestamps

### 12.2 Invalid Stored Timestamps

If stored timestamp cannot be parsed:
- Treat as "no timestamp" for that table
- Fall back to compare keys or TRUNCATE mode
- Log warning to UI

---

## 13. Performance Expectations

### 15.1 Best Case (5% changes)

| Operation | Current | Optimized |
|-----------|---------|-----------|
| Tier2 Query | 100% data | 5% data (Query 2) + small metadata (Query 1) |
| AxDB Delete | 100% delete | 5% targeted delete |
| AxDB Insert | 100% insert | 5% insert |
| **Total Time** | 100% | ~15-20% |

### 13.2 Worst Case (>40% changes)

Falls back to TRUNCATE mode, similar to current performance but with small overhead for control query.

### 13.3 Memory Usage

Control query adds ~16 bytes per record (RecId 8 bytes + SysRowVersion 8 bytes).
For 500k records: ~8MB additional memory for control data.

---

## 14. Migration Notes

### 14.1 Existing Configurations

- Existing configurations with `days:`, `where:`, `all` strategies will fail validation
- Users must update strategy overrides to use new format (RecId or SQL)
- Empty timestamp fields will trigger TRUNCATE + full insert on first sync
- Subsequent syncs will use optimized mode (timestamps stored after first sync)

### 14.2 SysRowVersion Field Handling

- Remove `SYSROWVERSION` from default "Fields to Exclude"
- Add logic to include SysRowVersion in control query but exclude from data copy
- SysRowVersion column should NOT be copied (it's a timestamp, will be regenerated)

### 14.3 Strategy Migration Examples

| Old Strategy | New Strategy |
|--------------|--------------|
| `CUSTTABLE\|days:30` | `CUSTTABLE\|sql:SELECT TOP @recordCount * FROM CUSTTABLE WHERE MODIFIEDDATETIME > DATEADD(day, -30, GETUTCDATE()) ORDER BY RecId DESC` |
| `CUSTTABLE\|where:DATAAREAID='1000'` | `CUSTTABLE\|sql:SELECT TOP @recordCount * FROM CUSTTABLE WHERE DATAAREAID='1000' ORDER BY RecId DESC` |
| `CUSTTABLE\|all` | `CUSTTABLE\|500000 -truncate` (or appropriate count) |
| `CUSTTABLE\|5000\|where:DATAAREAID='1000'` | `CUSTTABLE\|5000\|sql:SELECT TOP @recordCount * FROM CUSTTABLE WHERE DATAAREAID='1000' ORDER BY RecId DESC` |

---

## 15. Testing Scenarios

### 15.1 Scenario Matrix

| Scenario | Tier2 State | AxDB State | Expected Action |
|----------|-------------|------------|-----------------|
| First sync | Has data | Empty | TRUNCATE + insert, store timestamps |
| No changes | Unchanged | Unchanged | Skip (0 records to insert) |
| Tier2 updates | 5% changed | Unchanged | Incremental delete/insert |
| AxDB updates | Unchanged | 5% changed | Incremental delete/insert |
| Both changed | 5% each | 5% each | Incremental (10% total) |
| Major changes | 50% changed | - | TRUNCATE mode |
| Tier2 deletes | Records removed | Has extras | Delete extras |
| AxDB excess | Normal | +50% rows | TRUNCATE mode |
| No SysRowVersion | - | - | TRUNCATE + full insert |
| SQL strategy | Custom SQL | - | Same as RecId but with custom query |

### 15.2 Strategy Test Cases

| Test | Strategy | Expected Behavior |
|------|----------|-------------------|
| Default RecId | `CUSTTABLE` | Top N by RecId, N = DefaultRecordCount |
| Explicit RecId | `CUSTTABLE\|5000` | Top 5000 by RecId |
| SQL with count | `CUSTTABLE\|500\|sql:SELECT TOP @recordCount * FROM CUSTTABLE ORDER BY RecId DESC` | Top 500 using SQL |
| SQL without count | `CUSTTABLE\|sql:SELECT TOP @recordCount * FROM CUSTTABLE ORDER BY RecId DESC` | Top N using SQL, N = DefaultRecordCount |
| SQL with filter | `CUSTTABLE\|sql:SELECT TOP @recordCount * FROM CUSTTABLE WHERE DATAAREAID='1000' ORDER BY RecId DESC` | Top N filtered |
| Force truncate | `CUSTTABLE\|5000 -truncate` | Always TRUNCATE before insert |

---

## 16. Implementation Order

1. **Phase 1: Strategy Simplification**
   - Remove old strategy types (ModifiedDate, Where, RecIdWithWhere, ModifiedDateWithWhere, All)
   - Add new SQL strategy type
   - Update ParseStrategyLine in CopyOrchestrator
   - Update GenerateFetchSql and add GenerateControlSql
   - Remove FetchDataByModifiedDateAsync from Tier2DataService
   - Remove DeleteByModifiedDateAsync, DeleteByWhereClauseAsync from AxDbDataService
   - Update MainForm tooltip and add validation warning

2. **Phase 2: EstimatedSizeMB Fix**
   - Update calculation to use DefaultRecordCount for all strategies without explicit count
   - Ensure actual count updates after fetch (already implemented)

3. **Phase 3: Timestamp Infrastructure**
   - Add TimestampHelper static class
   - Add TimestampManager class
   - Update AppConfiguration model with new fields
   - Update UI with timestamp textareas and threshold control
   - Update ConfigManager to persist timestamps

4. **Phase 4: SysRowVersion Optimization**
   - Add FetchControlDataAsync to Tier2DataService
   - Add FetchDataByTimestampAsync to Tier2DataService
   - Add GetChangedCountAsync, GetRowCountAsync, GetRecIdSetAsync, GetMaxTimestampAsync to AxDbDataService
   - Add ExecuteIncrementalDeletesAsync with temp table logic to AxDbDataService
   - Add CreateTier2ControlTempTableAsync helper

5. **Phase 5: Orchestrator Integration**
   - Add UseOptimizedMode detection in PrepareTableListAsync
   - Implement ProcessTableOptimizedAsync with two-query approach
   - Implement ProcessTableTruncateOnlyAsync for non-optimized tables
   - Add change volume evaluation logic
   - Update timestamp storage on successful completion

6. **Phase 6: Testing & Refinement**
   - Test all scenarios from matrix
   - Test SQL strategy with various query types
   - Performance tuning
   - Edge case handling

---

*End of Specification*
