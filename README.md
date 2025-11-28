# D365FO Database Copy Tool

A WinForms .NET 9 application for copying data from Dynamics 365 Finance & Operations Azure SQL Database (Tier2) to local SQL Server (AxDB).

## Overview

This tool helps developers synchronize data from D365FO cloud environments to their local development databases, making it easier to test with production-like data.

## Features

### Core Functionality
- **Selective Table Copying**: Use patterns to include/exclude tables (e.g., `CUST*`, `Sys*`)
- **Two Copy Strategies**:
  - **RecId Strategy**: Copy last N records by RecId (e.g., last 10,000 records)
  - **ModifiedDate Strategy**: Copy records modified in last N days
- **Smart Field Mapping**: Automatically maps common fields between source and destination
- **Parallel Execution**: Configurable parallel fetch and insert operations for performance
- **SQLDICTIONARY Caching**: Dramatically faster table preparation (10-20x speedup)
- **Truncate Optimization**: Automatically uses TRUNCATE for full table copies

### User Interface
- **Configuration Management**: Save and load multiple connection configurations
- **Real-time Progress**: Monitor fetch/insert progress for each table
- **Sortable Grid**: Click column headers to sort, right-click to copy table names
- **Detailed Logging**: All SQL operations logged with timestamps
- **Menu System**: File menu (Save, Load, Exit) and Help menu (About)

### Technical Features
- Automatic sequence updates for D365FO tables
- Trigger management (disable during insert, re-enable after)
- Bulk insert with SqlBulkCopy for performance
- Transaction-based operations with rollback on errors
- Connection pooling for optimal performance

## Requirements

- Windows OS
- .NET 9.0 Runtime
- SQL Server 2019+ (for local AxDB)
- Access to D365FO Azure SQL Database (Tier2)

## Configuration

### Connection Settings
- **Tier2 (Azure SQL)**: Server, database, credentials, timeouts
- **AxDB (Local SQL)**: Server, database, credentials, timeouts
- **Execution**: Parallel fetch/insert connection counts

### Table Selection
- **Tables to Copy**: Patterns like `*`, `CUST*`, `SALES*` (one per line)
- **Tables to Exclude**: Default excludes `Sys*`, `Batch*`, `*Staging`
- **Fields to Exclude**: Fields to skip (e.g., `SYSROWVERSION`)

### Copy Strategies
- **Default**: RecId strategy with configurable record count
- **Per-Table Overrides**:
  - `CUSTTABLE:5000` - Copy last 5000 records by RecId
  - `SALESLINE:days:30` - Copy records modified in last 30 days

## Usage

1. **Configure Connections**: Set up Tier2 and AxDB connection details
2. **Select Tables**: Define inclusion/exclusion patterns
3. **Prepare Table List**: Discovers tables and validates schemas
4. **Get Data**: Fetches data from Tier2 in parallel
5. **Insert Data**: Inserts fetched data into AxDB in parallel

Or use **Run All** to execute all steps sequentially.

## Version

- **Format**: `1.0.YYYY.DayOfYear` (auto-increments with each build)
- **Example**: `1.0.2025.334`

## Author

**Denis Trunin**

- GitHub: https://github.com/TrudAX/
- Copyright © 2025 Denis Trunin

## License

MIT License - See LICENSE file for details

## Notes

- Configuration files are stored in `configs/` directory (excluded from git)
- Always test with non-production data first
- Ensure proper database permissions before running
- Large tables may take significant time to copy
