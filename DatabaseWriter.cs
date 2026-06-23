using System.Data;
using Microsoft.Data.Sqlite;

namespace HarddriveDeduper;

/// <summary>
/// Ensures the destination table exists and streams <see cref="FileRecord"/> rows into it, flushing in
/// batches. Each drive scanned in parallel gets its own writer (and connection); all writers share one
/// SQLite file, so every write is serialized through a process-wide lock (<see cref="_writeLock"/>) to
/// keep concurrent writers from colliding. Reads run lock-free against WAL snapshots.
/// </summary>
public sealed class DatabaseWriter : IDisposable
{
    /// <summary>Max length of the <c>FileName</c> column; longer names are truncated before insert.</summary>
    private const int FileNameMaxLength = 260;

    private readonly Options _options;
    private readonly SemaphoreSlim _writeLock;
    private readonly SqliteConnection _connection;
    private readonly DataTable _buffer;

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

    /// <param name="writeLock">
    /// The shared write lock guarding the single database file. Every drive's writer is constructed with
    /// the same instance so their batched writes serialize rather than collide.
    /// </param>
    public DatabaseWriter(Options options, SemaphoreSlim writeLock)
    {
        _options = options;
        _writeLock = writeLock;
        _connection = new SqliteConnection(options.ConnectionString);
        _buffer = BuildSchemaTable();
    }

    /// <summary>
    /// Open this writer's connection without creating or migrating any schema. Used for the extra
    /// per-drive writers once a first writer has run <see cref="InitializeAsync"/> to set up the tables,
    /// so drives scanning in parallel each get an independent connection (and WAL read snapshot).
    /// </summary>
    public async Task OpenConnectionAsync(CancellationToken ct)
    {
        await _connection.OpenAsync(ct);
        await Database.ConfigureAsync(_connection, ct);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _connection.OpenAsync(ct);
        await Database.ConfigureAsync(_connection, ct);

        if (_options.Recreate)
        {
            await ExecuteAsync(DropTableSql(), ct);
            await ExecuteAsync(DropSkipsTableSql(), ct);
        }

        await ExecuteAsync(CreateTableSql(), ct);
        await ExecuteAsync(CreateSkipsTableSql(), ct);

        // The scan log is an audit history across runs, so it is created but never dropped here.
        await ExecuteAsync(CreateScansTableSql(), ct);
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

        await _writeLock.WaitAsync(ct);
        try
        {
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
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Stamp this scan run as finished: sets the completion time, final status (e.g. <c>Completed</c>,
    /// <c>Canceled</c>, <c>Failed</c>), the number of file rows written, and any fatal error message.
    /// </summary>
    public async Task CompleteScanAsync(string status, string? error, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
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
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Persist the directories skipped during the scan, tagged with this run's <c>ScanRunId</c> so they
    /// can be correlated with the file rows. One-shot insert; safe to call with an empty set.
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

        await BulkInsertAsync(table, _options.SkipTableName, ct);
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

        int count = _buffer.Rows.Count;
        await BulkInsertAsync(_buffer, _options.TableName, ct);
        RowsWritten += count;
        _rowsWrittenThisScan += count;
        _buffer.Clear();
    }

    // --- pass two: hashing ----------------------------------------------

    /// <summary>
    /// Read the next chunk of this scan run's rows to hash, paging forward by <c>Id</c> so every row
    /// is returned exactly once regardless of whether it has been hashed yet. Pass <c>0</c> for
    /// <paramref name="afterId"/> on the first call, then the last returned Id on each subsequent call;
    /// an empty result means the pass is done. Paging by Id (rather than filtering on a NULL hash)
    /// avoids re-reading size-skipped rows forever. This is a read, so it runs lock-free against a WAL
    /// snapshot while another drive may be writing.
    /// </summary>
    public async Task<IReadOnlyList<PendingHash>> ReadNextHashChunkAsync(long afterId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
SELECT Id, FullPath, SizeBytes
FROM {_options.TableName}
WHERE ScanRunId = @id AND Id > @afterId
ORDER BY Id
LIMIT @take;";
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
    /// Apply a batch of computed hashes to their rows in a single transaction, each row updated by a
    /// reused prepared statement. Safe to call with an empty set. Hashing errors are recorded without
    /// clobbering an existing pass-one error.
    /// </summary>
    public async Task UpdateHashesAsync(IReadOnlyCollection<HashResult> results, CancellationToken ct)
    {
        if (results.Count == 0)
            return;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
UPDATE {_options.TableName}
SET ContentHash = @hash,
    ScanError   = IFNULL(ScanError, @error)
WHERE Id = @id;";
            SqliteParameter pId = cmd.Parameters.Add("@id", SqliteType.Integer);
            SqliteParameter pHash = cmd.Parameters.Add("@hash", SqliteType.Text);
            SqliteParameter pError = cmd.Parameters.Add("@error", SqliteType.Text);
            cmd.Prepare();

            foreach (HashResult r in results)
            {
                pId.Value = r.Id;
                pHash.Value = (object?)r.ContentHash ?? DBNull.Value;
                pError.Value = (object?)r.Error ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
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
    /// Insert the folder fingerprint rows for this scan run into the file table, tagged with
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

        await BulkInsertAsync(table, _options.TableName, ct);
        FoldersWritten += folders.Count;
    }

    // --- helpers ---------------------------------------------------------

    /// <summary>
    /// Insert every row of <paramref name="table"/> into <paramref name="destination"/> in a single
    /// transaction via one reused prepared statement (the fast path for SQLite). Column names map
    /// positionally to <c>@</c>-prefixed parameters. Guarded by the shared write lock.
    /// </summary>
    private async Task BulkInsertAsync(DataTable table, string destination, CancellationToken ct)
    {
        if (table.Rows.Count == 0)
            return;

        var columns = table.Columns.Cast<DataColumn>().ToArray();
        string columnList = string.Join(", ", columns.Select(c => c.ColumnName));
        string valueList = string.Join(", ", columns.Select(c => "@" + c.ColumnName));

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {destination} ({columnList}) VALUES ({valueList});";

            // No fixed SqliteType: each parameter's type is inferred from its Value (long -> INTEGER,
            // DateTime -> ISO TEXT, string -> TEXT, DBNull -> NULL), matching the DataTable columns.
            var parameters = new SqliteParameter[columns.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                SqliteParameter p = cmd.CreateParameter();
                p.ParameterName = "@" + columns[i].ColumnName;
                cmd.Parameters.Add(p);
                parameters[i] = p;
            }
            cmd.Prepare();

            foreach (DataRow row in table.Rows)
            {
                for (int i = 0; i < parameters.Length; i++)
                    parameters[i].Value = row[i];
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
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

    private string DropTableSql() => $"DROP TABLE IF EXISTS {_options.TableName};";

    private string CreateTableSql() => $@"
CREATE TABLE IF NOT EXISTS {_options.TableName} (
    Id              INTEGER PRIMARY KEY,         -- auto-incrementing rowid alias
    EntryType       TEXT    NOT NULL,            -- 'F' = file, 'D' = folder fingerprint
    FileName        TEXT    NOT NULL,
    FullPath        TEXT    NOT NULL,
    SizeBytes       INTEGER NOT NULL,
    DateModifiedUtc TEXT    NOT NULL,
    DateCreatedUtc  TEXT    NOT NULL,
    ContentHash     TEXT    NULL,
    ScanError       TEXT    NULL,
    ScanRunId       TEXT    NOT NULL,
    ScannedAtUtc    TEXT    NOT NULL
);
-- Duplicate analysis groups by (EntryType, ContentHash, SizeBytes), so lead with those columns.
CREATE INDEX IF NOT EXISTS IX_Files_Dup       ON {_options.TableName} (EntryType, ContentHash, SizeBytes) WHERE ContentHash IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_Files_SizeBytes ON {_options.TableName} (SizeBytes);";

    private string DropSkipsTableSql() => $"DROP TABLE IF EXISTS {_options.SkipTableName};";

    private string CreateSkipsTableSql() => $@"
CREATE TABLE IF NOT EXISTS {_options.SkipTableName} (
    Id           INTEGER PRIMARY KEY,
    FullPath     TEXT NOT NULL,
    Reason       TEXT NOT NULL,
    ScanRunId    TEXT NOT NULL,
    ScannedAtUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ScanSkips_ScanRunId ON {_options.SkipTableName} (ScanRunId);";

    private string CreateScansTableSql() => $@"
CREATE TABLE IF NOT EXISTS {_options.ScanTableName} (
    ScanRunId      TEXT    NOT NULL PRIMARY KEY,
    StartedAtUtc   TEXT    NOT NULL,
    CompletedAtUtc TEXT    NULL,
    Status         TEXT    NOT NULL,
    MachineName    TEXT    NULL,
    Drive          TEXT    NULL,
    Roots          TEXT    NULL,
    ComputeHash    INTEGER NOT NULL,
    FilesWritten   INTEGER NULL,
    ScanError      TEXT    NULL
);
CREATE INDEX IF NOT EXISTS IX_Scans_Status ON {_options.ScanTableName} (Status);
-- Speeds up ""latest completed scan per drive"" lookups during analysis.
CREATE INDEX IF NOT EXISTS IX_Scans_Drive ON {_options.ScanTableName} (Drive, Status, CompletedAtUtc);";

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => _connection.Dispose();
}
