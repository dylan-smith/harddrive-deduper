using Microsoft.Data.SqlClient;

namespace HarddriveDeduper;

/// <summary>
/// What a cleanup will remove. The retained runs are the most recent <em>completed</em> scan of each
/// drive (exactly the set <see cref="DuplicateAnalyzer"/> would analyze); everything else is droppable.
/// </summary>
public sealed record CleanupPlan(
    IReadOnlyList<ScanRef> KeptScans,
    long FilesToDelete,
    long SkipsToDelete);

/// <summary>What a cleanup actually removed.</summary>
public sealed record CleanupResult(long FilesDeleted, long SkipsDeleted);

/// <summary>
/// Trims the file inventory down to the data worth keeping, so the database doesn't grow without bound
/// as scans accumulate. The retained set is the most recent <em>completed</em> scan run for each drive
/// — the same runs <see cref="DuplicateAnalyzer"/> selects. Rows in the file and skip tables belonging
/// to any other run (older completed runs, canceled/failed/partial runs, or runs no longer in the scan
/// log) are deleted. The <c>dbo.Scans</c> audit log is left untouched: it is one small row per run and
/// is intentionally a permanent history.
/// </summary>
/// <remarks>
/// Cleanup assumes no scan is currently running — any data present therefore belongs to a finished run
/// (completed or otherwise). It considers every drive in the log regardless of <c>--drives</c>, so it
/// never deletes a drive's retained run just because that drive wasn't named on the command line.
/// </remarks>
public sealed class DatabaseCleaner
{
    private readonly Options _options;

    public DatabaseCleaner(Options options) => _options = options;

    /// <summary>
    /// Work out what cleanup would remove without changing anything: the runs to retain plus the number
    /// of file and skip rows that would be deleted. Throws if there is no scan log to consult, or if no
    /// completed run exists at all (deleting every file row is almost never the intent — run a scan to
    /// completion first, or use <c>--recreate</c> on a normal scan to wipe deliberately).
    /// </summary>
    public async Task<CleanupPlan> PlanAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);

        if (!await TableExistsAsync(conn, _options.ScanTableName, ct))
            throw new InvalidOperationException(
                $"Scan log '{_options.ScanTableName}' does not exist; cannot determine which runs to keep.");

        List<ScanRef> kept = await GetLatestCompletedScansAsync(conn, ct);
        if (kept.Count == 0)
            throw new InvalidOperationException(
                "No completed scan run was found, so there is nothing to retain. Refusing to delete all " +
                "file data. Run a scan to completion first, or use --recreate on a scan to wipe deliberately.");

        long files = await TableExistsAsync(conn, _options.TableName, ct)
            ? await CountNotKeptAsync(conn, _options.TableName, ct)
            : 0;
        long skips = await TableExistsAsync(conn, _options.SkipTableName, ct)
            ? await CountNotKeptAsync(conn, _options.SkipTableName, ct)
            : 0;

        return new CleanupPlan(kept, files, skips);
    }

    /// <summary>
    /// Carry out the deletions described by <paramref name="plan"/>. Rows are removed in batches (of
    /// <see cref="Options.BatchSize"/>) so a large purge doesn't balloon the transaction log or hold one
    /// long lock; <paramref name="progress"/> receives the running deleted-row total after each batch.
    /// </summary>
    public async Task<CleanupResult> ExecuteAsync(CleanupPlan plan, IProgress<long>? progress, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);

        long files = await TableExistsAsync(conn, _options.TableName, ct)
            ? await DeleteNotKeptAsync(conn, _options.TableName, progress, ct)
            : 0;
        long skips = await TableExistsAsync(conn, _options.SkipTableName, ct)
            ? await DeleteNotKeptAsync(conn, _options.SkipTableName, progress, ct)
            : 0;

        return new CleanupResult(files, skips);
    }

    /// <summary>
    /// The runs to retain: the most recent completed scan of each drive. Mirrors
    /// <see cref="DuplicateAnalyzer.GetLatestCompletedScansAsync"/> but spans every drive (cleanup is
    /// database-wide), so the keep set here matches the <c>Keep</c> CTE used by the delete statements.
    /// </summary>
    private async Task<List<ScanRef>> GetLatestCompletedScansAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT Drive, ScanRunId, CompletedAtUtc
FROM (
    SELECT Drive, ScanRunId, CompletedAtUtc,
           ROW_NUMBER() OVER (PARTITION BY Drive ORDER BY CompletedAtUtc DESC, ScanRunId) AS rn
    FROM {_options.ScanTableName}
    WHERE Status = 'Completed' AND Drive IS NOT NULL
) ranked
WHERE rn = 1
ORDER BY Drive;";
        cmd.CommandTimeout = 0;

        var scans = new List<ScanRef>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            scans.Add(new ScanRef(reader.GetString(0), reader.GetString(1).TrimEnd(), reader.GetDateTime(2)));
        return scans;
    }

    private async Task<long> CountNotKeptAsync(SqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
{KeepCte()}
SELECT COUNT_BIG(*)
FROM {table} t
WHERE NOT EXISTS (SELECT 1 FROM Keep k WHERE k.ScanRunId = t.ScanRunId);";
        cmd.CommandTimeout = 0;

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is long total ? total : Convert.ToInt64(result);
    }

    private async Task<long> DeleteNotKeptAsync(
        SqlConnection conn, string table, IProgress<long>? progress, CancellationToken ct)
    {
        string sql = $@"
{KeepCte()}
DELETE TOP (@batch) t
FROM {table} t
WHERE NOT EXISTS (SELECT 1 FROM Keep k WHERE k.ScanRunId = t.ScanRunId);";

        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            cmd.Parameters.AddWithValue("@batch", _options.BatchSize);

            int deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted == 0)
                break;

            total += deleted;
            progress?.Report(total);
        }
        return total;
    }

    /// <summary>
    /// The <c>Keep</c> CTE: the ScanRunId of the most recent completed run per drive. Prepended to each
    /// count/delete statement so "rows not belonging to a retained run" is expressed with NOT EXISTS.
    /// </summary>
    private string KeepCte() => $@"
WITH Keep AS (
    SELECT ScanRunId
    FROM (
        SELECT ScanRunId,
               ROW_NUMBER() OVER (PARTITION BY Drive ORDER BY CompletedAtUtc DESC, ScanRunId) AS rn
        FROM {_options.ScanTableName}
        WHERE Status = 'Completed' AND Drive IS NOT NULL
    ) ranked
    WHERE rn = 1
)";

    private static async Task<bool> TableExistsAsync(SqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(@t, 'U') IS NULL THEN 0 ELSE 1 END;";
        cmd.Parameters.AddWithValue("@t", table);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) == 1;
    }
}
