using Microsoft.Data.SqlClient;

namespace HarddriveDeduper;

/// <summary>A set of identical files — same content hash and size — found at two or more locations.</summary>
public sealed class DuplicateGroup
{
    public required string ContentHash { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>One file name from the group (meaningful only when <see cref="DistinctNameCount"/> is 1).</summary>
    public required string FileName { get; init; }

    /// <summary>How many distinct file names the copies use; &gt; 1 means the name varies across locations.</summary>
    public required int DistinctNameCount { get; init; }

    /// <summary>Number of distinct locations this content lives at.</summary>
    public required int CopyCount { get; init; }

    /// <summary>Reclaimable space: the redundant copies (count − 1) × file size.</summary>
    public required long WastedBytes { get; init; }

    /// <summary>A handful of the locations, for display. May be fewer than <see cref="CopyCount"/>.</summary>
    public List<string> SamplePaths { get; } = new();
}

/// <summary>
/// The outcome of a duplicate analysis: the duplicate sets plus which scan run they came from.
/// <see cref="ScanRunId"/> is null when the table holds no data yet.
/// </summary>
/// <param name="TotalWastedBytes">
/// Reclaimable space across <em>every</em> duplicate set in the run, not just the ones in
/// <see cref="Groups"/>. This is the grand total the user could recover by deduplicating.
/// </param>
public sealed record DuplicateAnalysis(
    string? ScanRunId,
    DateTime? ScannedAtUtc,
    long TotalWastedBytes,
    IReadOnlyList<DuplicateGroup> Groups);

/// <summary>
/// Queries the scanned file table for content that exists in multiple locations and ranks the worst
/// offenders by wasted space. Only the most recent <em>completed</em> scan run is considered (per the
/// scan log), so files that were present in an earlier scan but have since been deleted from disk are
/// excluded, and partial data from a scan that never finished is never analyzed. Files are considered
/// identical when their content hash and size both match; rows without a hash are ignored.
/// </summary>
public sealed class DuplicateAnalyzer
{
    private readonly Options _options;

    public DuplicateAnalyzer(Options options) => _options = options;

    /// <summary>
    /// Find the <paramref name="topN"/> duplicate sets with the most wasted space within the latest
    /// scan run, attaching up to <paramref name="samplePathsPerGroup"/> example locations to each.
    /// </summary>
    public async Task<DuplicateAnalysis> FindTopDuplicatesAsync(
        int topN, int samplePathsPerGroup, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);

        (string? runId, DateTime? scannedAt) = await GetLatestCompletedScanAsync(conn, ct);
        if (runId is null)
            throw new InvalidOperationException(
                $"No completed scan found in '{_options.ScanTableName}'. Run a scan to completion before analyzing " +
                "(scans that were canceled, failed, or never finished are not eligible for analysis).");

        long totalWasted = await QueryTotalWastedAsync(conn, runId, ct);
        List<DuplicateGroup> groups = await QueryTopGroupsAsync(conn, runId, topN, ct);

        foreach (DuplicateGroup g in groups)
            await LoadSamplePathsAsync(conn, runId, g, samplePathsPerGroup, ct);

        return new DuplicateAnalysis(runId, scannedAt, totalWasted, groups);
    }

    /// <summary>
    /// The run to analyze: the most recent <em>completed</em> scan from the scan log, or (null, null)
    /// if none exists. Only completed runs are eligible — partial data from a canceled, failed, or
    /// never-finished scan is never analyzed.
    /// </summary>
    private async Task<(string? RunId, DateTime? CompletedAt)> GetLatestCompletedScanAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // Guard against the log table not existing (older databases / analyze-only runs).
        cmd.CommandText = $@"
IF OBJECT_ID('{_options.ScanTableName}', 'U') IS NOT NULL
    SELECT TOP (1) ScanRunId, CompletedAtUtc
    FROM {_options.ScanTableName}
    WHERE Status = 'Completed'
    ORDER BY CompletedAtUtc DESC;";
        cmd.CommandTimeout = 0;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (null, null);

        return (reader.GetString(0).TrimEnd(), reader.GetDateTime(1));
    }

    /// <summary>
    /// Sum the reclaimable space over <em>all</em> duplicate sets in the run — every set of identical
    /// content contributes (copies − 1) × size. This is the total before the top-N cut is applied.
    /// </summary>
    private async Task<long> QueryTotalWastedAsync(SqlConnection conn, string runId, CancellationToken ct)
    {
        string sql = $@"
SELECT COALESCE(SUM(WastedBytes), 0)
FROM (
    SELECT CAST(COUNT(*) - 1 AS BIGINT) * SizeBytes AS WastedBytes
    FROM {_options.TableName}
    WHERE ScanRunId = @runId AND ContentHash IS NOT NULL
    GROUP BY ContentHash, SizeBytes
    HAVING COUNT(*) > 1
) AS perGroup;";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@runId", runId);

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is long total ? total : Convert.ToInt64(result);
    }

    private async Task<List<DuplicateGroup>> QueryTopGroupsAsync(
        SqlConnection conn, string runId, int topN, CancellationToken ct)
    {
        string sql = $@"
SELECT TOP (@topN)
    ContentHash,
    SizeBytes,
    COUNT(*)                                  AS CopyCount,
    CAST(COUNT(*) - 1 AS BIGINT) * SizeBytes  AS WastedBytes,
    MIN(FileName)                             AS SampleName,
    COUNT(DISTINCT FileName)                  AS DistinctNameCount
FROM {_options.TableName}
WHERE ScanRunId = @runId AND ContentHash IS NOT NULL
GROUP BY ContentHash, SizeBytes
HAVING COUNT(*) > 1
ORDER BY WastedBytes DESC;";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@topN", topN);
        cmd.Parameters.AddWithValue("@runId", runId);

        var groups = new List<DuplicateGroup>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            groups.Add(new DuplicateGroup
            {
                ContentHash = reader.GetString(0).TrimEnd(),
                SizeBytes = reader.GetInt64(1),
                CopyCount = reader.GetInt32(2),
                WastedBytes = reader.GetInt64(3),
                FileName = reader.GetString(4),
                DistinctNameCount = reader.GetInt32(5),
            });
        }
        return groups;
    }

    private async Task LoadSamplePathsAsync(
        SqlConnection conn, string runId, DuplicateGroup g, int limit, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT TOP (@limit) FullPath
FROM {_options.TableName}
WHERE ScanRunId = @runId AND ContentHash = @hash AND SizeBytes = @size
ORDER BY FullPath;";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@hash", g.ContentHash);
        cmd.Parameters.AddWithValue("@size", g.SizeBytes);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            g.SamplePaths.Add(reader.GetString(0));
    }
}
