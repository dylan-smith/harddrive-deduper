using System.Data;
using Microsoft.Data.SqlClient;

namespace HarddriveDeduper;

/// <summary>
/// Ensures the destination table exists and streams <see cref="FileRecord"/> rows into it
/// efficiently via <see cref="SqlBulkCopy"/>, flushing in batches.
/// </summary>
public sealed class DatabaseWriter : IDisposable
{
    /// <summary>Max length of the <c>FileName</c> column; longer names are truncated before insert.</summary>
    private const int FileNameMaxLength = 260;

    /// <summary>Creates the target database (named in the connection string) if it doesn't exist.</summary>
    private const string CreateDatabaseIfMissingSql = @"
IF DB_ID(@db) IS NULL
BEGIN
    DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@db);
    EXEC sp_executesql @sql;
END";

    /// <summary>Session-scoped staging table that pass two bulk-loads hashes into before a set-based UPDATE join.</summary>
    private const string HashStagingTable = "#HashUpdates";

    private readonly Options _options;
    private readonly SqlConnection _connection;
    private readonly DataTable _buffer;

    /// <summary>Reusable staging buffer for pass-two hash updates; created by <see cref="BeginHashPassAsync"/>.</summary>
    private DataTable? _hashBuffer;

    /// <summary>ScanRunId of the drive currently being scanned. Reset by each <see cref="BeginScanAsync"/>.</summary>
    private string _scanRunId = string.Empty;

    /// <summary>Rows written for the current drive's scan run; reset by each <see cref="BeginScanAsync"/>.</summary>
    private long _rowsWrittenThisScan;

    /// <summary>
    /// Rows written for the current drive's scan run so far — also the total number of rows pass two
    /// will page through to hash, so it serves as the denominator for the hash-pass progress bar.
    /// </summary>
    public long RowsWrittenThisScan => _rowsWrittenThisScan;

    /// <summary>Total rows written across every drive scanned this process (for the final summary).</summary>
    public long RowsWritten;

    /// <summary>Total folder fingerprint rows written across every drive scanned this process.</summary>
    public long FoldersWritten;

    public DatabaseWriter(Options options)
    {
        _options = options;
        _connection = new SqlConnection(options.ConnectionString);
        _buffer = BuildSchemaTable();
    }

    /// <summary>
    /// Open this writer's connection without creating, dropping, or migrating any schema. Used for the
    /// extra per-drive writers once a first writer has run <see cref="InitializeAsync"/> to set up the
    /// tables, so drives scanning in parallel each get an independent connection (and therefore their
    /// own session-scoped hash-staging temp table).
    /// </summary>
    public Task OpenConnectionAsync(CancellationToken ct) => _connection.OpenAsync(ct);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await EnsureDatabaseExistsAsync(ct);
        await _connection.OpenAsync(ct);

        if (_options.Recreate)
        {
            await ExecuteAsync(DropTableSql(), ct);
            await ExecuteAsync(DropSkipsTableSql(), ct);
        }

        await ExecuteAsync(CreateTableSql(), ct);
        // Bring an older file table (created before folder fingerprints) up to the current schema.
        await ExecuteAsync(MigrateFilesTableSql(), ct);
        await ExecuteAsync(CreateSkipsTableSql(), ct);

        // The scan log is an audit history across runs, so it is created but never dropped here.
        await ExecuteAsync(CreateScansTableSql(), ct);
        // Bring an older scan log (created before per-drive scans) up to the current schema.
        await ExecuteAsync(MigrateScansTableSql(), ct);
    }

    /// <summary>
    /// Record the start of a scan run for a single <paramref name="drive"/>: generates a fresh
    /// <c>ScanRunId</c> and inserts a row tagged <c>Running</c> with no completion time. Every drive is
    /// its own scan run, so subsequent rows are tagged with this id until the next <see cref="BeginScanAsync"/>.
    /// A row that stays <c>Running</c> (or keeps a NULL <c>CompletedAtUtc</c>) marks a scan that never
    /// finished — e.g. the process was killed — and is excluded from analysis.
    /// </summary>
    public async Task BeginScanAsync(string drive, CancellationToken ct)
    {
        _scanRunId = Guid.NewGuid().ToString("N");
        _rowsWrittenThisScan = 0;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO {_options.ScanTableName}
    (ScanRunId, StartedAtUtc, Status, MachineName, Drive, Roots, ComputeHash)
VALUES (@id, @started, @status, @machine, @drive, @roots, @hash);";
        cmd.Parameters.AddWithValue("@id", _scanRunId);
        cmd.Parameters.AddWithValue("@started", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@status", "Running");
        cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
        cmd.Parameters.AddWithValue("@drive", drive);
        cmd.Parameters.AddWithValue("@roots", drive);
        cmd.Parameters.AddWithValue("@hash", _options.ComputeHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Stamp this scan run as finished: sets the completion time, final status (e.g. <c>Completed</c>,
    /// <c>Canceled</c>, <c>Failed</c>), the number of file rows written, and any fatal error message.
    /// </summary>
    public async Task CompleteScanAsync(string status, string? error, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
UPDATE {_options.ScanTableName}
SET CompletedAtUtc = @completed,
    Status         = @status,
    FilesWritten   = @files,
    ScanError      = @error
WHERE ScanRunId = @id;";
        cmd.Parameters.AddWithValue("@completed", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@files", _rowsWrittenThisScan);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", _scanRunId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Persist the directories skipped during the scan, tagged with this run's <c>ScanRunId</c> so they
    /// can be correlated with the file rows. One-shot bulk insert; safe to call with an empty set.
    /// </summary>
    public async Task WriteSkipsAsync(IReadOnlyCollection<SkipRecord> skips, CancellationToken ct)
    {
        if (skips.Count == 0)
            return;

        var table = new DataTable();
        table.Columns.Add("FullPath", typeof(string));
        table.Columns.Add("Reason", typeof(string));
        table.Columns.Add("ScanRunId", typeof(string));
        table.Columns.Add("ScannedAtUtc", typeof(DateTime));

        DateTime now = DateTime.UtcNow;
        foreach (SkipRecord s in skips)
        {
            DataRow row = table.NewRow();
            row["FullPath"] = s.FullPath;
            row["Reason"] = s.Reason;
            row["ScanRunId"] = _scanRunId;
            row["ScannedAtUtc"] = now;
            table.Rows.Add(row);
        }

        using var bulk = new SqlBulkCopy(_connection)
        {
            DestinationTableName = _options.SkipTableName,
            BulkCopyTimeout = 0,
            BatchSize = _options.BatchSize,
        };
        foreach (DataColumn col in table.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        await bulk.WriteToServerAsync(table, ct);
    }

    /// <summary>
    /// Pass one: add a metadata record to the buffer; flushes automatically once the batch size is
    /// reached. The content hash is left NULL here and filled in later by the pass-two hashing.
    /// </summary>
    public async Task AddAsync(FileRecord r, CancellationToken ct)
    {
        DataRow row = _buffer.NewRow();
        row["EntryType"] = "F";
        row["FileName"] = Truncate(r.FileName, FileNameMaxLength);
        row["FullPath"] = r.FullPath;
        row["SizeBytes"] = r.SizeBytes;
        row["DateModifiedUtc"] = r.DateModifiedUtc;
        row["DateCreatedUtc"] = r.DateCreatedUtc;
        row["ContentHash"] = DBNull.Value;
        row["ScanError"] = DBNull.Value;
        row["ScanRunId"] = _scanRunId;
        row["ScannedAtUtc"] = DateTime.UtcNow;
        _buffer.Rows.Add(row);

        if (_buffer.Rows.Count >= _options.BatchSize)
            await FlushAsync(ct);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (_buffer.Rows.Count == 0)
            return;

        using var bulk = new SqlBulkCopy(_connection)
        {
            DestinationTableName = _options.TableName,
            BulkCopyTimeout = 0,
            BatchSize = _options.BatchSize,
        };
        foreach (DataColumn col in _buffer.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        await bulk.WriteToServerAsync(_buffer, ct);
        RowsWritten += _buffer.Rows.Count;
        _rowsWrittenThisScan += _buffer.Rows.Count;
        _buffer.Clear();
    }

    // --- pass two: hashing ----------------------------------------------

    /// <summary>
    /// Prepare for the hashing pass: create the session-scoped staging table that
    /// <see cref="UpdateHashesAsync"/> bulk-loads into, and reset its in-memory buffer. Call once
    /// per drive before paging through its rows with <see cref="ReadNextHashChunkAsync"/>.
    /// </summary>
    public async Task BeginHashPassAsync(CancellationToken ct)
    {
        await ExecuteAsync($@"
IF OBJECT_ID('tempdb..{HashStagingTable}') IS NOT NULL DROP TABLE {HashStagingTable};
CREATE TABLE {HashStagingTable} (
    Id          BIGINT        NOT NULL PRIMARY KEY,
    ContentHash CHAR(64)      NULL,
    ScanError   NVARCHAR(MAX) NULL
);", ct);

        _hashBuffer ??= BuildHashStagingTable();
        _hashBuffer.Clear();
    }

    /// <summary>
    /// Read the next chunk of this scan run's rows to hash, paging forward by <c>Id</c> so every row
    /// is returned exactly once regardless of whether it has been hashed yet. Pass <c>0</c> for
    /// <paramref name="afterId"/> on the first call, then the last returned Id on each subsequent call;
    /// an empty result means the pass is done. Paging by Id (rather than filtering on a NULL hash)
    /// avoids re-reading size-skipped rows forever and keeps each read fully complete before the
    /// matching update runs, so the read never contends with the update on the same connection.
    /// </summary>
    public async Task<IReadOnlyList<PendingHash>> ReadNextHashChunkAsync(long afterId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
SELECT TOP (@take) Id, FullPath, SizeBytes
FROM {_options.TableName}
WHERE ScanRunId = @id AND Id > @afterId
ORDER BY Id;";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@take", _options.BatchSize);
        cmd.Parameters.AddWithValue("@id", _scanRunId);
        cmd.Parameters.AddWithValue("@afterId", afterId);

        var chunk = new List<PendingHash>(_options.BatchSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            chunk.Add(new PendingHash(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        return chunk;
    }

    /// <summary>
    /// Apply a batch of computed hashes to their rows: bulk-load them into the staging table, then a
    /// single set-based UPDATE join fills in the hash (and any hashing error). Safe to call with an
    /// empty set. Hashing errors are recorded without clobbering an existing pass-one error.
    /// </summary>
    public async Task UpdateHashesAsync(IReadOnlyCollection<HashResult> results, CancellationToken ct)
    {
        if (results.Count == 0)
            return;

        _hashBuffer!.Clear();
        foreach (HashResult r in results)
        {
            DataRow row = _hashBuffer.NewRow();
            row["Id"] = r.Id;
            row["ContentHash"] = (object?)r.ContentHash ?? DBNull.Value;
            row["ScanError"] = (object?)r.Error ?? DBNull.Value;
            _hashBuffer.Rows.Add(row);
        }

        await ExecuteAsync($"TRUNCATE TABLE {HashStagingTable};", ct);

        using (var bulk = new SqlBulkCopy(_connection)
        {
            DestinationTableName = HashStagingTable,
            BulkCopyTimeout = 0,
            BatchSize = _options.BatchSize,
        })
        {
            foreach (DataColumn col in _hashBuffer.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(_hashBuffer, ct);
        }

        await ExecuteAsync($@"
UPDATE f
SET f.ContentHash = h.ContentHash,
    f.ScanError   = ISNULL(f.ScanError, h.ScanError)
FROM {_options.TableName} f
INNER JOIN {HashStagingTable} h ON f.Id = h.Id;", ct);
    }

    // --- pass three: folder fingerprints --------------------------------

    /// <summary>
    /// Stream this scan run's file rows ordered by content hash, invoking <paramref name="onFile"/> for
    /// each. Ordering by hash means each folder's descendant hashes arrive sorted, so the fingerprinter
    /// produces a deterministic result; the reader stays forward-only so memory stays bounded. Folder
    /// rows (<c>EntryType = 'D'</c>) are excluded so re-running the pass never folds folders into folders.
    /// </summary>
    public async Task StreamHashedFilesAsync(Action<HashedFile> onFile, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
SELECT FullPath, ContentHash, SizeBytes, DateModifiedUtc, DateCreatedUtc
FROM {_options.TableName}
WHERE ScanRunId = @id AND EntryType = 'F'
ORDER BY ContentHash;";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@id", _scanRunId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string? hash = reader.IsDBNull(1) ? null : reader.GetString(1).TrimEnd();
            onFile(new HashedFile(
                reader.GetString(0), hash, reader.GetInt64(2), reader.GetDateTime(3), reader.GetDateTime(4)));
        }
    }

    /// <summary>
    /// Bulk-insert the folder fingerprint rows for this scan run into the file table, tagged with
    /// <c>EntryType = 'D'</c> and this run's <c>ScanRunId</c>. Safe to call with an empty set.
    /// </summary>
    public async Task WriteFoldersAsync(IReadOnlyCollection<FolderRecord> folders, CancellationToken ct)
    {
        if (folders.Count == 0)
            return;

        var table = new DataTable();
        foreach (DataColumn col in _buffer.Columns)
            table.Columns.Add(col.ColumnName, col.DataType);

        DateTime now = DateTime.UtcNow;
        foreach (FolderRecord f in folders)
        {
            DataRow row = table.NewRow();
            row["EntryType"] = "D";
            row["FileName"] = Truncate(f.FileName, FileNameMaxLength);
            row["FullPath"] = f.FullPath;
            row["SizeBytes"] = f.SizeBytes;
            row["DateModifiedUtc"] = f.DateModifiedUtc;
            row["DateCreatedUtc"] = f.DateCreatedUtc;
            row["ContentHash"] = (object?)f.ContentHash ?? DBNull.Value;
            row["ScanError"] = DBNull.Value;
            row["ScanRunId"] = _scanRunId;
            row["ScannedAtUtc"] = now;
            table.Rows.Add(row);
        }

        using var bulk = new SqlBulkCopy(_connection)
        {
            DestinationTableName = _options.TableName,
            BulkCopyTimeout = 0,
            BatchSize = _options.BatchSize,
        };
        foreach (DataColumn col in table.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        await bulk.WriteToServerAsync(table, ct);
        FoldersWritten += folders.Count;
    }

    // --- helpers ---------------------------------------------------------

    private static DataTable BuildHashStagingTable()
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(long));
        t.Columns.Add("ContentHash", typeof(string));
        t.Columns.Add("ScanError", typeof(string));
        return t;
    }

    private static DataTable BuildSchemaTable()
    {
        var t = new DataTable();
        t.Columns.Add("EntryType", typeof(string));
        t.Columns.Add("FileName", typeof(string));
        t.Columns.Add("FullPath", typeof(string));
        t.Columns.Add("SizeBytes", typeof(long));
        t.Columns.Add("DateModifiedUtc", typeof(DateTime));
        t.Columns.Add("DateCreatedUtc", typeof(DateTime));
        t.Columns.Add("ContentHash", typeof(string));
        t.Columns.Add("ScanError", typeof(string));
        t.Columns.Add("ScanRunId", typeof(string));
        t.Columns.Add("ScannedAtUtc", typeof(DateTime));
        return t;
    }

    private string DropTableSql() =>
        $"IF OBJECT_ID('{_options.TableName}', 'U') IS NOT NULL DROP TABLE {_options.TableName};";

    private string CreateTableSql() => $@"
IF OBJECT_ID('{_options.TableName}', 'U') IS NULL
BEGIN
    CREATE TABLE {_options.TableName} (
        Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        EntryType       CHAR(1)        NOT NULL,  -- 'F' = file, 'D' = folder fingerprint
        FileName        NVARCHAR({FileNameMaxLength})  NOT NULL,
        FullPath        NVARCHAR(MAX)  NOT NULL,
        SizeBytes       BIGINT         NOT NULL,
        DateModifiedUtc DATETIME2(3)   NOT NULL,
        DateCreatedUtc  DATETIME2(3)   NOT NULL,
        ContentHash     CHAR(64)       NULL,
        ScanError       NVARCHAR(MAX)  NULL,
        ScanRunId       CHAR(32)       NOT NULL,
        ScannedAtUtc    DATETIME2(3)   NOT NULL
    );
    -- Duplicate analysis groups by (EntryType, ContentHash, SizeBytes), so lead with those columns.
    CREATE INDEX IX_Files_Dup       ON {_options.TableName} (EntryType, ContentHash, SizeBytes) WHERE ContentHash IS NOT NULL;
    CREATE INDEX IX_Files_SizeBytes ON {_options.TableName} (SizeBytes);
END";

    /// <summary>
    /// Add the <c>EntryType</c> discriminator to a file table created before folder fingerprints existed.
    /// Pre-existing rows are all files, so the column defaults to <c>'F'</c> (which backfills them).
    /// </summary>
    private string MigrateFilesTableSql() => $@"
IF OBJECT_ID('{_options.TableName}', 'U') IS NOT NULL
   AND COL_LENGTH('{_options.TableName}', 'EntryType') IS NULL
BEGIN
    ALTER TABLE {_options.TableName} ADD EntryType CHAR(1) NOT NULL DEFAULT 'F';
END";

    private string DropSkipsTableSql() =>
        $"IF OBJECT_ID('{_options.SkipTableName}', 'U') IS NOT NULL DROP TABLE {_options.SkipTableName};";

    private string CreateSkipsTableSql() => $@"
IF OBJECT_ID('{_options.SkipTableName}', 'U') IS NULL
BEGIN
    CREATE TABLE {_options.SkipTableName} (
        Id           BIGINT IDENTITY(1,1) PRIMARY KEY,
        FullPath     NVARCHAR(MAX)  NOT NULL,
        Reason       NVARCHAR(MAX)  NOT NULL,
        ScanRunId    CHAR(32)       NOT NULL,
        ScannedAtUtc DATETIME2(3)   NOT NULL
    );
    CREATE INDEX IX_ScanSkips_ScanRunId ON {_options.SkipTableName} (ScanRunId);
END";

    private string CreateScansTableSql() => $@"
IF OBJECT_ID('{_options.ScanTableName}', 'U') IS NULL
BEGIN
    CREATE TABLE {_options.ScanTableName} (
        ScanRunId      CHAR(32)      NOT NULL PRIMARY KEY,
        StartedAtUtc   DATETIME2(3)  NOT NULL,
        CompletedAtUtc DATETIME2(3)  NULL,
        Status         NVARCHAR(20)  NOT NULL,
        MachineName    NVARCHAR(256) NULL,
        Drive          NVARCHAR(260) NULL,
        Roots          NVARCHAR(MAX) NULL,
        ComputeHash    BIT           NOT NULL,
        FilesWritten   BIGINT        NULL,
        ScanError      NVARCHAR(MAX) NULL
    );
    CREATE INDEX IX_Scans_Status ON {_options.ScanTableName} (Status);
    -- Speeds up ""latest completed scan per drive"" lookups during analysis.
    CREATE INDEX IX_Scans_Drive ON {_options.ScanTableName} (Drive, Status, CompletedAtUtc);
END";

    /// <summary>
    /// Add the per-drive <c>Drive</c> column to a scan log created before per-drive scans existed.
    /// Pre-existing rows keep a NULL <c>Drive</c> and are simply not matched by per-drive analysis.
    /// </summary>
    private string MigrateScansTableSql() => $@"
IF OBJECT_ID('{_options.ScanTableName}', 'U') IS NOT NULL
   AND COL_LENGTH('{_options.ScanTableName}', 'Drive') IS NULL
BEGIN
    ALTER TABLE {_options.ScanTableName} ADD Drive NVARCHAR(260) NULL;
END";

    /// <summary>Create the target database if the connection points at one that doesn't exist yet.</summary>
    private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(_options.ConnectionString);
        string dbName = builder.InitialCatalog;
        if (string.IsNullOrEmpty(dbName))
            return; // nothing named to create

        builder.InitialCatalog = "master";
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync(ct);
        await using var cmd = master.CreateCommand();
        cmd.CommandText = CreateDatabaseIfMissingSql;
        cmd.Parameters.AddWithValue("@db", dbName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => _connection.Dispose();
}
