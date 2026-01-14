# DBSyncTool Optimization Testing Guide

This guide covers testing procedures for the newly implemented optimizations.

## Overview of Changes

The tool now supports:
1. **Simplified Strategy System**: RecId and Sql strategies only
2. **SysRowVersion Optimization**: Intelligent change detection for incremental sync
3. **Smart Mode Selection**: TRUNCATE vs INCREMENTAL based on change volume

## Pre-Testing Checklist

- [ ] Backup current configuration files from `Config/` folder
- [ ] Have access to both Tier2 (Azure SQL) and AxDB (local SQL Server)
- [ ] Ensure test tables have SysRowVersion column for optimization testing
- [ ] Note baseline performance metrics for comparison

## Test Scenarios

### Scenario 1: Strategy Simplification

**Objective**: Verify new strategy syntax works correctly

**Test Cases**:

1. **Default RecId Strategy**
   ```
   Strategy Override: CUSTTABLE
   Expected: Uses DefaultRecordCount (10000)
   ```

2. **RecId with Explicit Count**
   ```
   Strategy Override: CUSTTABLE|5000
   Expected: Fetches top 5000 records by RecId
   ```

3. **SQL Strategy without Count**
   ```
   Strategy Override: CUSTTABLE|sql:SELECT * FROM CUSTTABLE WHERE DATAAREAID='USMF'
   Expected: Uses DefaultRecordCount, applies WHERE filter
   ```

4. **SQL Strategy with Count**
   ```
   Strategy Override: CUSTTABLE|3000|sql:SELECT * FROM CUSTTABLE WHERE BLOCKED=0
   Expected: Fetches top 3000 records matching condition
   ```

5. **Truncate Flag**
   ```
   Strategy Override: VENDTABLE|5000 -truncate
   Expected: Uses TRUNCATE instead of DELETE
   ```

6. **NoCompare Flag**
   ```
   Strategy Override: INVENTTABLE|10000 -nocompare
   Expected: Skips delta comparison
   ```

**Verification**:
- [ ] Check log output for generated SQL
- [ ] Use "Get SQL" context menu to preview queries
- [ ] Verify correct number of records copied
- [ ] Confirm WHERE clauses are applied correctly

---

### Scenario 2: First Run (No Optimization)

**Objective**: Verify standard mode works when no timestamps exist

**Setup**:
1. Clear timestamp fields in configuration:
   - Tier2Timestamps: (empty)
   - AxDBTimestamps: (empty)
2. Select a table with SysRowVersion column

**Expected Behavior**:
- UseOptimizedMode = false
- Standard fetch → insert flow
- No control query executed
- Full data transfer

**Verification**:
- [ ] Log shows standard mode messages
- [ ] No "Optimized mode" or "Control query" messages
- [ ] Data copied successfully
- [ ] Sequences updated correctly

---

### Scenario 3: Optimized Mode - No Changes

**Objective**: Verify INCREMENTAL mode skips fetch when no changes detected

**Setup**:
1. Run Scenario 2 first to establish timestamps
2. Immediately run the same table again without modifying data

**Expected Behavior**:
- UseOptimizedMode = true
- Control query executes (~1KB transfer)
- Change detection: Tier2=0, AxDB=0, Percent=0%
- INCREMENTAL mode selected
- No records fetched or inserted
- Timestamps updated

**Verification**:
- [ ] Log shows "Optimized mode: Fetching control data"
- [ ] Log shows "Tier2 changes: 0, AxDB changes: 0"
- [ ] Log shows "Using INCREMENTAL mode"
- [ ] Log shows "No records to insert"
- [ ] FetchTimeSeconds minimal (~1-2 seconds for control query only)
- [ ] InsertTimeSeconds = 0

---

### Scenario 4: Optimized Mode - Low Changes (INCREMENTAL)

**Objective**: Verify INCREMENTAL mode with minimal data transfer

**Setup**:
1. Run Scenario 2 to establish baseline
2. In Tier2, update 50-100 records (< 40% threshold)
3. In AxDB, update 20-30 records
4. Run sync

**Expected Behavior**:
- Control query shows changed records
- ChangePercent < TruncateThresholdPercent (40%)
- INCREMENTAL mode selected
- Only changed records fetched (using timestamp filter)
- Incremental deletes executed (3 steps)
- Only missing records inserted

**Verification**:
- [ ] Log shows change counts: "Tier2 changes: X, AxDB changes: Y"
- [ ] ChangePercent calculated correctly
- [ ] Log shows "Using INCREMENTAL mode"
- [ ] Log shows "Delete Tier2-modified", "Delete AxDB-modified", "Delete not-in-Tier2"
- [ ] Log shows "Fetching X missing records"
- [ ] RecordsFetched matches expected count
- [ ] Timestamps updated after success

**Sample Log Output**:
```
[CUSTTABLE] Optimized mode: Fetching control data
[CUSTTABLE] Control query: 5000 records in 1.23s
[CUSTTABLE] Tier2 changes: 75, AxDB changes: 30, Total: 2.1%, Excess: 0.0%
[CUSTTABLE] Using INCREMENTAL mode
[AxDB] Creating temp table for CUSTTABLE
[AxDB SQL] Delete Tier2-modified: ...
[AxDB SQL] Delete AxDB-modified: ...
[AxDB SQL] Delete not-in-Tier2: ...
[CUSTTABLE] Fetching 75 missing records
[CUSTTABLE] Completed (INCREMENTAL mode)
```

---

### Scenario 5: Optimized Mode - High Changes (TRUNCATE)

**Objective**: Verify TRUNCATE mode when changes exceed threshold

**Setup**:
1. Run Scenario 2 to establish baseline
2. In Tier2, update > 40% of records (e.g., 2500 out of 5000)
3. Run sync

**Expected Behavior**:
- Control query executes
- ChangePercent > TruncateThresholdPercent (40%)
- TRUNCATE mode selected
- Full data fetch (fallback to standard mode)
- Table truncated before insert
- Timestamps updated

**Verification**:
- [ ] Log shows "Total: X%"  (X > 40)
- [ ] Log shows "Using TRUNCATE mode (threshold: 40%)"
- [ ] Log shows truncate operation
- [ ] All records copied
- [ ] UsedTruncate flag = true

---

### Scenario 6: Configuration Persistence

**Objective**: Verify timestamps are saved and reloaded correctly

**Test Steps**:
1. Run sync with optimization enabled
2. Check configuration file JSON for timestamp fields
3. Close and restart application
4. Load same configuration
5. Verify timestamps are present

**Verification**:
- [ ] `Tier2Timestamps` field contains entries like:
   ```
   CUSTTABLE,0x0000000012345678
   VENDTABLE,0x00000000ABCDEF12
   ```
- [ ] `AxDBTimestamps` field populated similarly
- [ ] After reload, UseOptimizedMode = true for tables with timestamps
- [ ] Timestamps used correctly in next sync

---

### Scenario 7: Error Handling

**Objective**: Verify rollback and cleanup on errors

**Test Cases**:

1. **Connection Lost During Incremental Sync**
   - Disconnect network during INCREMENTAL mode
   - Expected: Transaction rollback, triggers re-enabled
   - Verify: Table not corrupted, can retry

2. **Invalid SQL Strategy**
   ```
   Strategy: CUSTTABLE|sql:INVALID SQL SYNTAX
   ```
   - Expected: Error logged, table status = FetchError
   - Verify: Other tables continue processing

3. **Timestamp Format Corruption**
   - Manually edit config with invalid timestamp
   - Expected: Falls back to standard mode
   - Verify: No crash, UseOptimizedMode = false

**Verification**:
- [ ] No data corruption
- [ ] Error messages clear and actionable
- [ ] Triggers always re-enabled (check: `SELECT * FROM sys.triggers WHERE is_disabled = 1`)
- [ ] Can retry failed operations

---

### Scenario 8: Performance Validation

**Objective**: Measure performance improvements

**Baseline Test** (without optimization):
1. Clear timestamps
2. Sync a 50,000 row table
3. Record: Total time, network bytes transferred

**Optimized Test** (no changes):
1. Re-sync same table (0% changes)
2. Record: Total time, network bytes transferred

**Expected Results**:
- Control query: ~50KB transfer (vs ~5GB baseline)
- Total time: < 5 seconds (vs minutes baseline)
- 99%+ reduction in data transfer

**Optimized Test** (5% changes):
1. Update 5% of records
2. Re-sync
3. Record metrics

**Expected Results**:
- Transfer: ~5% of baseline
- Time: ~10-15% of baseline
- Only changed records transferred

---

## Integration Points to Verify

### CopyOrchestrator Integration
- [ ] TimestampManager loaded from config at startup
- [ ] Timestamps saved after successful sync
- [ ] PrepareTableListAsync detects optimized mode capability
- [ ] ProcessSingleTableAsync routes correctly based on UseOptimizedMode flag

### Tier2DataService Integration
- [ ] FetchControlDataAsync returns RecId + SysRowVersion columns only
- [ ] FetchDataByTimestampAsync applies timestamp filter correctly
- [ ] SQL queries include proper ORDER BY RecId DESC

### AxDbDataService Integration
- [ ] GetChangedCountAsync counts correctly
- [ ] ExecuteIncrementalDeletesAsync performs 3-step delete
- [ ] GetMaxTimestampAsync returns correct value after sync
- [ ] GetRecIdSetAsync retrieves all RecIds efficiently

---

## Common Issues and Solutions

### Issue: Optimization not triggering
**Symptoms**: Always uses standard mode
**Checks**:
- Is table missing SysRowVersion column?
- Are timestamps missing from config?
- Check log for "SysRowVersion not found"

### Issue: High change percentage unexpected
**Symptoms**: Always uses TRUNCATE mode
**Checks**:
- Verify timestamps are being updated after successful sync
- Check for bulk updates in either database
- Review TruncateThresholdPercent setting (default 40%)

### Issue: Missing records after sync
**Symptoms**: Record count mismatch
**Checks**:
- Review incremental delete SQL in logs
- Verify RecId overlap between Tier2 and AxDB
- Check for RecId gaps or reuse

### Issue: Timestamp not updating
**Symptoms**: Same timestamps after multiple syncs
**Checks**:
- Verify sync completed successfully (Status = Inserted)
- Check for exceptions during timestamp save
- Confirm GetMaxTimestampAsync returns non-null

---

## Performance Benchmarks

### Expected Improvements

| Scenario | Standard Mode | Optimized Mode | Improvement |
|----------|--------------|----------------|-------------|
| No Changes (10K rows) | 30s, 100MB | 2s, 100KB | 15x faster, 1000x less data |
| 5% Changes (10K rows) | 30s, 100MB | 5s, 5MB | 6x faster, 20x less data |
| 50% Changes (10K rows) | 30s, 100MB | 25s, 90MB | Falls back to TRUNCATE mode |

*Actual results vary based on network, table structure, and data complexity*

---

## Regression Testing

Verify existing functionality still works:

- [ ] Delta comparison v2 (RECVERSION + datetime fields)
- [ ] Parallel processing (multiple tables simultaneously)
- [ ] Sequence updates after insert
- [ ] Trigger disable/enable
- [ ] Get SQL context menu feature
- [ ] Configuration save/load
- [ ] Strategy override parsing
- [ ] Field exclusions
- [ ] Table pattern matching

---

## Sign-off Checklist

Before deploying to production:

- [ ] All test scenarios pass
- [ ] Performance improvements validated
- [ ] Error handling tested
- [ ] Configuration persistence verified
- [ ] No regression in existing features
- [ ] Documentation updated
- [ ] Backup strategy in place

---

## Rollback Plan

If issues arise in production:

1. Revert to previous version
2. Existing configs will work (new fields ignored)
3. Timestamps lost, will rebuild on first run
4. No data corruption expected (transactions + rollback)

---

## Sample Configurations

See `SAMPLE_CONFIGS.md` for example configurations covering various use cases.
