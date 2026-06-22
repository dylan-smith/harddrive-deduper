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

/// <summary>One drive's scan run that fed an analysis: its drive root, scan id and completion time.</summary>
public sealed record ScanRef(string Drive, string ScanRunId, DateTime CompletedAtUtc);

/// <summary>
/// The outcome of a duplicate analysis: the duplicate sets plus the per-drive scan runs they were
/// combined from. <see cref="Scans"/> is empty when no completed scans exist.
/// </summary>
/// <param name="TotalWastedBytes">
/// Reclaimable space across <em>every</em> duplicate set in the combined runs, not just the ones in
/// <see cref="Groups"/>. This is the grand total the user could recover by deduplicating.
/// </param>
/// <param name="FolderGroups">
/// Duplicate <em>folders</em> — directory trees whose entire content (the set of all descendant file
/// hashes) is identical — ranked by wasted space. Nested copies are pruned: a folder is never grouped
/// with its own ancestor or descendant (only the topmost of any such chain is kept), so every set lists
/// independently deletable trees. These bytes overlap <see cref="Groups"/> and <see cref="TotalWastedBytes"/>
/// (the same redundant files, viewed at folder granularity); they let a whole redundant tree be reclaimed
/// at once rather than file by file.
/// </param>
public sealed record DuplicateAnalysis(
    IReadOnlyList<ScanRef> Scans,
    long TotalWastedBytes,
    IReadOnlyList<DuplicateGroup> Groups,
    IReadOnlyList<DuplicateGroup> FolderGroups);

/// <summary>
/// Queries the scanned file table for content that exists in multiple locations and ranks the worst
/// offenders by wasted space. The most recent <em>completed</em> scan run for <em>each drive</em> is
/// selected (per the scan log) and the runs are combined, so duplicates are detected across drives.
/// When <c>--drives</c> is given, only those drives' latest scans are combined. Files present in an
/// earlier scan of a drive but since deleted are excluded (an older run is superseded), and partial
/// data from a scan that never finished is never analyzed. Files are considered identical when their
/// content hash and size both match; rows without a hash are ignored.
/// </summary>
public sealed class DuplicateAnalyzer
{
    private readonly Options _options;

    public DuplicateAnalyzer(Options options) => _options = options;

    /// <summary>
    /// Find the <paramref name="topN"/> duplicate sets with the most wasted space across the latest
    /// completed scan of each drive (filtered by <c>--drives</c> when given), attaching up to
    /// <paramref name="samplePathsPerGroup"/> example locations to each.
    /// </summary>
    public async Task<DuplicateAnalysis> FindTopDuplicatesAsync(
        int topN, int samplePathsPerGroup, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);

        List<ScanRef> scans = await GetLatestCompletedScansAsync(conn, ct);
        if (scans.Count == 0)
        {
            string scope = _options.Drives.Count > 0
                ? $" for the selected drive(s): {string.Join(", ", _options.Drives)}"
                : "";
            throw new InvalidOperationException(
                $"No completed scan found in '{_options.ScanTableName}'{scope}. Run a scan to completion before analyzing " +
                "(scans that were canceled, failed, or never finished are not eligible for analysis).");
        }

        var runIds = scans.Select(s => s.ScanRunId).ToList();
        long totalWasted = await QueryTotalWastedAsync(conn, runIds, ct);
        List<DuplicateGroup> groups = await QueryTopFileGroupsAsync(conn, runIds, topN, ct);
        List<DuplicateGroup> folderGroups = await QueryTopFolderGroupsAsync(conn, runIds, topN, ct);

        foreach (DuplicateGroup g in groups)
            await LoadFileSamplePathsAsync(conn, runIds, g, samplePathsPerGroup, ct);
        foreach (DuplicateGroup g in folderGroups)
            await LoadFolderSamplePathsAsync(conn, runIds, g, samplePathsPerGroup, ct);

        return new DuplicateAnalysis(scans, totalWasted, groups, folderGroups);
    }

    /// <summary>
    /// The runs to analyze: the most recent <em>completed</em> scan for each drive, or an empty list
    /// if none exist. When <c>--drives</c> is given, only those drives are considered. Only completed
    /// runs are eligible — partial data from a canceled, failed, or never-finished scan is never analyzed.
    /// </summary>
    private async Task<List<ScanRef>> GetLatestCompletedScansAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();

        // Optionally restrict to the drives the user named on the command line.
        string driveFilter = "";
        if (_options.Drives.Count > 0)
        {
            var names = new string[_options.Drives.Count];
            for (int i = 0; i < _options.Drives.Count; i++)
            {
                names[i] = "@drive" + i;
                cmd.Parameters.AddWithValue("@drive" + i, _options.Drives[i]);
            }
            driveFilter = " AND Drive IN (" + string.Join(", ", names) + ")";
        }

        // One row per drive — the newest completed run (ROW_NUMBER breaks any same-timestamp tie so a
        // drive never contributes two runs, which would double-count files). Guard against the log table
        // not existing (older databases / analyze-only runs).
        cmd.CommandText = $@"
IF OBJECT_ID('{_options.ScanTableName}', 'U') IS NOT NULL
    SELECT Drive, ScanRunId, CompletedAtUtc
    FROM (
        SELECT Drive, ScanRunId, CompletedAtUtc,
               ROW_NUMBER() OVER (PARTITION BY Drive ORDER BY CompletedAtUtc DESC, ScanRunId) AS rn
        FROM {_options.ScanTableName}
        WHERE Status = 'Completed' AND Drive IS NOT NULL{driveFilter}
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

    /// <summary>
    /// Sum the reclaimable space over <em>all</em> duplicate sets across the combined runs — every set
    /// of identical content contributes (copies − 1) × size. This is the total before the top-N cut.
    /// </summary>
    private async Task<long> QueryTotalWastedAsync(SqlConnection conn, IReadOnlyList<string> runIds, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        string inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
SELECT COALESCE(SUM(WastedBytes), 0)
FROM (
    SELECT CAST(COUNT(*) - 1 AS BIGINT) * SizeBytes AS WastedBytes
    FROM {_options.TableName}
    WHERE ScanRunId IN ({inClause}) AND ContentHash IS NOT NULL AND EntryType = 'F'
    GROUP BY ContentHash, SizeBytes
    HAVING COUNT(*) > 1
) AS perGroup;";
        cmd.CommandTimeout = 0;

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is long total ? total : Convert.ToInt64(result);
    }

    private async Task<List<DuplicateGroup>> QueryTopFileGroupsAsync(
        SqlConnection conn, IReadOnlyList<string> runIds, int topN, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        string inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
SELECT TOP (@topN)
    ContentHash,
    SizeBytes,
    COUNT(*)                                  AS CopyCount,
    CAST(COUNT(*) - 1 AS BIGINT) * SizeBytes  AS WastedBytes,
    MIN(FileName)                             AS SampleName,
    COUNT(DISTINCT FileName)                  AS DistinctNameCount
FROM {_options.TableName}
WHERE ScanRunId IN ({inClause}) AND ContentHash IS NOT NULL AND EntryType = 'F'
GROUP BY ContentHash, SizeBytes
HAVING COUNT(*) > 1
ORDER BY WastedBytes DESC;";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@topN", topN);

        return await ReadGroupsAsync(cmd, ct);
    }

    /// <summary>
    /// Duplicate folders, ranked by wasted space, with nested copies pruned: within each set of
    /// same-fingerprint folders, any folder that is a descendant of another in the set is dropped (only
    /// the topmost is kept), then sets with fewer than two remaining copies are discarded. This stops a
    /// folder being reported as a duplicate of its own ancestor/descendant — e.g. a folder whose only
    /// child is a subfolder with the same content — which can't be deleted to reclaim space.
    /// </summary>
    private async Task<List<DuplicateGroup>> QueryTopFolderGroupsAsync(
        SqlConnection conn, IReadOnlyList<string> runIds, int topN, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        string inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
{IndependentFoldersCte(inClause)}
SELECT TOP (@topN)
    ContentHash,
    SizeBytes,
    COUNT(*)                                  AS CopyCount,
    CAST(COUNT(*) - 1 AS BIGINT) * SizeBytes  AS WastedBytes,
    MIN(FileName)                             AS SampleName,
    COUNT(DISTINCT FileName)                  AS DistinctNameCount
FROM Independent
GROUP BY ContentHash, SizeBytes
HAVING COUNT(*) > 1
ORDER BY WastedBytes DESC;";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@topN", topN);

        return await ReadGroupsAsync(cmd, ct);
    }

    private static async Task<List<DuplicateGroup>> ReadGroupsAsync(SqlCommand cmd, CancellationToken ct)
    {
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

    private async Task LoadFileSamplePathsAsync(
        SqlConnection conn, IReadOnlyList<string> runIds, DuplicateGroup g, int limit, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        string inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
SELECT TOP (@limit) FullPath
FROM {_options.TableName}
WHERE ScanRunId IN ({inClause}) AND ContentHash = @hash AND SizeBytes = @size AND EntryType = 'F'
ORDER BY FullPath;";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@hash", g.ContentHash);
        cmd.Parameters.AddWithValue("@size", g.SizeBytes);

        await ReadSamplePathsAsync(cmd, g, ct);
    }

    /// <summary>Sample locations for a duplicate folder set, listing only the pruned (topmost) copies.</summary>
    private async Task LoadFolderSamplePathsAsync(
        SqlConnection conn, IReadOnlyList<string> runIds, DuplicateGroup g, int limit, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        string inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
{IndependentFoldersCte(inClause)}
SELECT TOP (@limit) FullPath
FROM Independent
WHERE ContentHash = @hash AND SizeBytes = @size
ORDER BY FullPath;";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@hash", g.ContentHash);
        cmd.Parameters.AddWithValue("@size", g.SizeBytes);

        await ReadSamplePathsAsync(cmd, g, ct);
    }

    private static async Task ReadSamplePathsAsync(SqlCommand cmd, DuplicateGroup g, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            g.SamplePaths.Add(reader.GetString(0));
    }

    /// <summary>
    /// A CTE named <c>Independent</c> over this run's hashed folder rows, excluding any folder that is a
    /// descendant of another folder sharing its fingerprint and size. "Descendant" is a pure path-prefix
    /// test: <c>f</c> is under <c>a</c> when <c>a</c>'s path is a prefix of <c>f</c>'s ending at a path
    /// separator (so <c>C:\foo</c> is an ancestor of <c>C:\foo\bar</c> but not of <c>C:\foobar</c>). The
    /// run-id <c>IN</c> clause is shared with the caller's command, which must have added those parameters.
    /// </summary>
    private string IndependentFoldersCte(string inClause) => $@"
WITH F AS (
    SELECT FullPath, FileName, ContentHash, SizeBytes
    FROM {_options.TableName}
    WHERE ScanRunId IN ({inClause}) AND ContentHash IS NOT NULL AND EntryType = 'D'
),
Independent AS (
    SELECT f.FullPath, f.FileName, f.ContentHash, f.SizeBytes
    FROM F f
    WHERE NOT EXISTS (
        SELECT 1 FROM F a
        WHERE a.ContentHash = f.ContentHash
          AND a.SizeBytes   = f.SizeBytes
          AND LEN(f.FullPath) > LEN(a.FullPath)
          AND LEFT(f.FullPath, LEN(a.FullPath)) = a.FullPath
          AND (RIGHT(a.FullPath, 1) = '\' OR SUBSTRING(f.FullPath, LEN(a.FullPath) + 1, 1) = '\')
    )
)";

    /// <summary>
    /// Add a <c>@run{i}</c> parameter for each scan id and return the comma-separated parameter list
    /// for use inside a <c>ScanRunId IN (...)</c> clause.
    /// </summary>
    private static string BuildRunIdInClause(SqlCommand cmd, IReadOnlyList<string> runIds)
    {
        var names = new string[runIds.Count];
        for (int i = 0; i < runIds.Count; i++)
        {
            names[i] = "@run" + i;
            cmd.Parameters.AddWithValue("@run" + i, runIds[i]);
        }
        return string.Join(", ", names);
    }
}
