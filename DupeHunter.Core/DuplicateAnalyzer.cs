using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;

namespace DupeHunter;

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
    public Collection<string> SamplePaths { get; } = [];
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
/// hashes) is identical — ranked by wasted space. Nested copies are pruned so only the highest-level
/// redundant trees survive: a folder is never grouped with its own ancestor or descendant, and a whole
/// set is dropped when every copy of it lives inside a copy of a larger duplicate set (deduplicating the
/// larger tree already reclaims it). So every set lists independently deletable trees and a sub-tree of
/// an already-listed duplicate folder is never reported on its own. These bytes overlap <see cref="Groups"/>
/// and <see cref="TotalWastedBytes"/>
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
        int topN, int samplePathsPerGroup, CancellationToken ct, IStepProgress? progress = null)
    {
        await using var conn = await Database.OpenConnectionAsync(_options, ct);

        progress?.BeginStep("Selecting the latest completed scan per drive…");
        var scans = await GetLatestCompletedScansAsync(conn, ct);
        if (scans.Count == 0)
        {
            var scope = _options.Drives.Count > 0
                ? $" for the selected drive(s): {string.Join(", ", _options.Drives)}"
                : "";
            throw new InvalidOperationException(
                $"No completed scan found in '{_options.ScanTableName}'{scope}. Run a scan to completion before analyzing " +
                "(scans that were canceled, failed, or never finished are not eligible for analysis).");
        }

        var runIds = scans.Select(s => s.ScanRunId).ToList();

        progress?.BeginStep("Tallying total wasted space…");
        var totalWasted = await QueryTotalWastedAsync(conn, runIds, ct);

        progress?.BeginStep("Finding duplicate files…");
        var groups = await QueryFileGroupsAsync(conn, runIds, topN, minWastedBytes: 0, ct);

        progress?.BeginStep("Finding duplicate folders…");
        var folderGroups = await QueryFolderGroupsAsync(conn, runIds, topN, minWastedBytes: 0, ct);

        await LoadAllSamplePathsAsync(conn, runIds, groups, folderGroups, samplePathsPerGroup, progress, ct);

        return new DuplicateAnalysis(scans, totalWasted, groups, folderGroups);
    }

    /// <summary>
    /// Find <em>every</em> duplicate file and folder set whose reclaimable (wasted) space is at least
    /// <paramref name="minWastedBytes"/>, across the latest completed scan of each drive (filtered by
    /// <c>--drives</c> when given). Unlike <see cref="FindTopDuplicatesAsync"/> this is uncapped and
    /// loads <em>all</em> of each set's locations, so the result is a complete, directly-actionable
    /// report rather than a console preview. Returns an empty analysis when no completed scans exist.
    /// </summary>
    public async Task<DuplicateAnalysis> FindDuplicatesOverThresholdAsync(
        long minWastedBytes, CancellationToken ct, IStepProgress? progress = null)
    {
        await using var conn = await Database.OpenConnectionAsync(_options, ct);

        progress?.BeginStep("Selecting the latest completed scan per drive…");
        var scans = await GetLatestCompletedScansAsync(conn, ct);
        if (scans.Count == 0)
        {
            return new DuplicateAnalysis(scans, 0, Array.Empty<DuplicateGroup>(), Array.Empty<DuplicateGroup>());
        }

        var runIds = scans.Select(s => s.ScanRunId).ToList();

        progress?.BeginStep("Tallying total wasted space…");
        var totalWasted = await QueryTotalWastedAsync(conn, runIds, ct);

        progress?.BeginStep("Finding duplicate files…");
        var groups = await QueryFileGroupsAsync(conn, runIds, topN: null, minWastedBytes, ct);

        progress?.BeginStep("Finding duplicate folders…");
        var folderGroups = await QueryFolderGroupsAsync(conn, runIds, topN: null, minWastedBytes, ct);

        // Load every location (limit 0 = unlimited) so the report lists all copies, not a sample.
        await LoadAllSamplePathsAsync(conn, runIds, groups, folderGroups, limit: 0, progress, ct);

        return new DuplicateAnalysis(scans, totalWasted, groups, folderGroups);
    }

    /// <summary>
    /// Load the locations for every file and folder set, reporting a running count as it goes (this can
    /// dominate the analysis when the uncapped report has many sets, each with all of its copies loaded).
    /// <paramref name="limit"/> ≤ 0 loads them all; otherwise at most that many per set.
    /// </summary>
    private async Task LoadAllSamplePathsAsync(
        SqliteConnection conn, IReadOnlyList<string> runIds,
        IReadOnlyList<DuplicateGroup> groups, IReadOnlyList<DuplicateGroup> folderGroups,
        int limit, IStepProgress? progress, CancellationToken ct)
    {
        if (groups.Count > 0)
        {
            progress?.BeginStep("Collecting file locations…");
            for (var i = 0; i < groups.Count; i++)
            {
                progress?.UpdateStep($"Collecting file locations ({i + 1}/{groups.Count})…");
                await LoadFileSamplePathsAsync(conn, runIds, groups[i], limit, ct);
            }
        }

        if (folderGroups.Count > 0)
        {
            progress?.BeginStep("Collecting folder locations…");
            for (var i = 0; i < folderGroups.Count; i++)
            {
                progress?.UpdateStep($"Collecting folder locations ({i + 1}/{folderGroups.Count})…");
                await LoadFolderSamplePathsAsync(conn, runIds, folderGroups[i], limit, ct);
            }
        }
    }

    /// <summary>
    /// The runs to analyze: the most recent <em>completed</em> scan for each drive, or an empty list
    /// if none exist. When <c>--drives</c> is given, only those drives are considered. Only completed
    /// runs are eligible — partial data from a canceled, failed, or never-finished scan is never analyzed.
    /// </summary>
    internal async Task<List<ScanRef>> GetLatestCompletedScansAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Guard against the log table not existing (fresh database / analyze-only on an unscanned file).
        if (!await TableExistsAsync(conn, _options.ScanTableName, ct))
        {
            return [];
        }

        await using var cmd = conn.CreateCommand();

        // Optionally restrict to the drives the user named on the command line.
        var driveFilter = "";
        if (_options.Drives.Count > 0)
        {
            var names = new string[_options.Drives.Count];
            for (var i = 0; i < _options.Drives.Count; i++)
            {
                names[i] = "@drive" + i;
                cmd.Parameters.AddWithValue("@drive" + i, _options.Drives[i]);
            }
            driveFilter = " AND Drive IN (" + string.Join(", ", names) + ")";
        }

        // One row per drive — the newest completed run (ROW_NUMBER breaks any same-timestamp tie so a
        // drive never contributes two runs, which would double-count files).
        cmd.CommandText = $@"
SELECT Drive, ScanRunId, CompletedAtUtc
FROM (
    SELECT Drive, ScanRunId, CompletedAtUtc,
           ROW_NUMBER() OVER (PARTITION BY Drive ORDER BY CompletedAtUtc DESC, ScanRunId) AS rn
    FROM {_options.ScanTableName}
    WHERE Status = 'Completed' AND Drive IS NOT NULL{driveFilter}
) ranked
WHERE rn = 1
ORDER BY Drive;";

        var scans = new List<ScanRef>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            scans.Add(new ScanRef(reader.GetString(0), reader.GetString(1).TrimEnd(), reader.GetDateTime(2)));
        }

        return scans;
    }

    /// <summary>
    /// Sum the reclaimable space over <em>all</em> duplicate sets across the combined runs — every set
    /// of identical content contributes (copies − 1) × size. This is the total before the top-N cut.
    /// </summary>
    private async Task<long> QueryTotalWastedAsync(SqliteConnection conn, IReadOnlyList<string> runIds, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
SELECT COALESCE(SUM(WastedBytes), 0)
FROM (
    SELECT (COUNT(*) - 1) * SizeBytes AS WastedBytes
    FROM {_options.TableName}
    WHERE ScanRunId IN ({inClause}) AND ContentHash IS NOT NULL AND EntryType = 'F'
    GROUP BY ContentHash, SizeBytes
    HAVING COUNT(*) > 1
) AS perGroup;";

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long total ? total : Convert.ToInt64(result);
    }

    /// <summary>
    /// Duplicate <em>file</em> sets ranked by wasted space. <paramref name="topN"/> caps the result
    /// (null = uncapped) and <paramref name="minWastedBytes"/> filters out sets reclaiming less than
    /// that (0 = no floor).
    /// </summary>
    private async Task<List<DuplicateGroup>> QueryFileGroupsAsync(
        SqliteConnection conn, IReadOnlyList<string> runIds, int? topN, long minWastedBytes, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
SELECT
    ContentHash,
    SizeBytes,
    COUNT(*)                    AS CopyCount,
    (COUNT(*) - 1) * SizeBytes  AS WastedBytes,
    MIN(FileName)               AS SampleName,
    COUNT(DISTINCT FileName)    AS DistinctNameCount
FROM {_options.TableName}
WHERE ScanRunId IN ({inClause}) AND ContentHash IS NOT NULL AND EntryType = 'F'
GROUP BY ContentHash, SizeBytes
HAVING COUNT(*) > 1 AND (COUNT(*) - 1) * SizeBytes >= @minWasted
ORDER BY WastedBytes DESC{LimitSuffix(cmd, topN)};";
        cmd.Parameters.AddWithValue("@minWasted", minWastedBytes);

        return await ReadGroupsAsync(cmd, ct);
    }

    /// <summary>
    /// Duplicate folders, ranked by wasted space, with nested copies pruned at two levels so that only
    /// the highest-level redundant trees are reported:
    /// <list type="bullet">
    /// <item><description><b>Same-fingerprint nesting</b>: within a set of identical folders, any folder
    /// that is a descendant of another in the same set is dropped (only the topmost is kept) — e.g. a
    /// folder whose only child is a subfolder with the same content.</description></item>
    /// <item><description><b>Cross-set containment</b>: a whole set is dropped when every one of its
    /// copies lives inside a copy of <em>another</em> duplicate set — i.e. it is a sub-tree of a larger
    /// duplicate folder. Deduplicating the larger set already reclaims it, so reporting it is redundant
    /// (e.g. <c>…\Airlift\Videos</c> when <c>…\Airlift</c> is itself a duplicate set).</description></item>
    /// </list>
    /// Sets with fewer than two remaining copies are then discarded. <paramref name="topN"/> caps the
    /// result (null = uncapped) and <paramref name="minWastedBytes"/> filters out sets reclaiming less
    /// than that (0 = no floor).
    /// </summary>
    private async Task<List<DuplicateGroup>> QueryFolderGroupsAsync(
        SqliteConnection conn, IReadOnlyList<string> runIds, int? topN, long minWastedBytes, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
{FolderCtesWithRedundancy(inClause)}
SELECT
    i.ContentHash,
    i.SizeBytes,
    COUNT(*)                      AS CopyCount,
    (COUNT(*) - 1) * i.SizeBytes  AS WastedBytes,
    MIN(i.FileName)               AS SampleName,
    COUNT(DISTINCT i.FileName)    AS DistinctNameCount
FROM Independent i
WHERE NOT EXISTS (SELECT 1 FROM Redundant r WHERE r.SHash = i.ContentHash AND r.SSize = i.SizeBytes)
GROUP BY i.ContentHash, i.SizeBytes
HAVING COUNT(*) > 1 AND (COUNT(*) - 1) * i.SizeBytes >= @minWasted
ORDER BY WastedBytes DESC{LimitSuffix(cmd, topN)};";
        cmd.Parameters.AddWithValue("@minWasted", minWastedBytes);

        return await ReadGroupsAsync(cmd, ct);
    }

    /// <summary>
    /// The trailing <c> LIMIT @topN</c> fragment for a ranked query, adding the parameter when capped; an
    /// empty string (no cap) when <paramref name="topN"/> is null. Appended after <c>ORDER BY</c>.
    /// </summary>
    private static string LimitSuffix(SqliteCommand cmd, int? topN)
    {
        if (topN is null)
        {
            return "";
        }

        cmd.Parameters.AddWithValue("@topN", topN.Value);
        return " LIMIT @topN";
    }

    /// <summary>True if a table of the given name exists in the database.</summary>
    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t;";
        cmd.Parameters.AddWithValue("@t", table);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task<List<DuplicateGroup>> ReadGroupsAsync(SqliteCommand cmd, CancellationToken ct)
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

    /// <summary>Locations for a duplicate file set. <paramref name="limit"/> ≤ 0 loads them all.</summary>
    private async Task LoadFileSamplePathsAsync(
        SqliteConnection conn, IReadOnlyList<string> runIds, DuplicateGroup g, int limit, CancellationToken ct)
    {
        foreach (var path in await QueryFilePathsAsync(conn, runIds, g.ContentHash, g.SizeBytes, limit, ct))
        {
            g.SamplePaths.Add(path);
        }
    }

    /// <summary>
    /// Locations for a duplicate folder set, listing only the pruned (topmost) copies.
    /// <paramref name="limit"/> ≤ 0 loads them all.
    /// </summary>
    private async Task LoadFolderSamplePathsAsync(
        SqliteConnection conn, IReadOnlyList<string> runIds, DuplicateGroup g, int limit, CancellationToken ct)
    {
        foreach (var path in await QueryFolderPathsAsync(conn, runIds, g.ContentHash, g.SizeBytes, limit, ct))
        {
            g.SamplePaths.Add(path);
        }
    }

    /// <summary>The sorted locations of one duplicate file set. <paramref name="limit"/> ≤ 0 loads them all.</summary>
    private async Task<List<string>> QueryFilePathsAsync(
        SqliteConnection conn, IReadOnlyList<string> runIds, string contentHash, long sizeBytes,
        int limit, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
SELECT FullPath
FROM {_options.TableName}
WHERE ScanRunId IN ({inClause}) AND ContentHash = @hash AND SizeBytes = @size AND EntryType = 'F'
ORDER BY FullPath{LimitSuffix(cmd, limit > 0 ? limit : null)};";
        cmd.Parameters.AddWithValue("@hash", contentHash);
        cmd.Parameters.AddWithValue("@size", sizeBytes);

        return await ReadPathsAsync(cmd, ct);
    }

    /// <summary>
    /// The sorted locations of one duplicate folder set — pruned (topmost) copies only.
    /// <paramref name="limit"/> ≤ 0 loads them all.
    /// </summary>
    private async Task<List<string>> QueryFolderPathsAsync(
        SqliteConnection conn, IReadOnlyList<string> runIds, string contentHash, long sizeBytes,
        int limit, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
{IndependentFoldersCte(inClause)}
SELECT FullPath
FROM Independent
WHERE ContentHash = @hash AND SizeBytes = @size
ORDER BY FullPath{LimitSuffix(cmd, limit > 0 ? limit : null)};";
        cmd.Parameters.AddWithValue("@hash", contentHash);
        cmd.Parameters.AddWithValue("@size", sizeBytes);

        return await ReadPathsAsync(cmd, ct);
    }

    private static async Task<List<string>> ReadPathsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            paths.Add(reader.GetString(0));
        }
        return paths;
    }

    /// <summary>
    /// A CTE named <c>Independent</c> over this run's hashed folder rows, excluding any folder that is a
    /// descendant of another folder sharing its fingerprint and size. Nesting is detected by matching a
    /// folder's <c>parent_path</c> against its parent's <c>FullPath</c> — an indexed equality join rather
    /// than a path-prefix string scan. This is exact: if <em>any</em> ancestor shares a folder's
    /// (fingerprint, size) then its immediate parent does too, because folder size is the monotonic sum of
    /// descendant file sizes and the fingerprint folds in the descendant count, so equal endpoints force
    /// an identical descendant multiset at every level in between (including the parent).
    /// <para>
    /// <c>F</c> is pre-filtered to <em>raw duplicate</em> folders — fingerprints occurring on 2+ folders
    /// in the base set. A unique-fingerprint folder can neither survive the final <c>HAVING COUNT(*) &gt; 1</c>
    /// nor be the ancestor that prunes another (that requires a shared fingerprint), so dropping it loses
    /// nothing while shrinking the working set drastically before any join.
    /// </para>
    /// <para>
    /// The run-id <c>IN</c> clause is shared with the caller's command, which must have added those
    /// parameters. <paramref name="materialize"/> forces SQLite to evaluate <c>Independent</c> once into a
    /// transient table; set it only when the CTE is referenced more than once downstream (see
    /// <see cref="FolderCtesWithRedundancy"/>). Leave it off for a single-reference use (e.g. the
    /// sample-paths query filters by hash), where inlining lets SQLite push the filter into the scan.
    /// </para>
    /// </summary>
    private string IndependentFoldersCte(string inClause, bool materialize = false) => $@"
WITH Base AS (
    SELECT FullPath, FileName, ContentHash, SizeBytes
    FROM {_options.TableName}
    WHERE ScanRunId IN ({inClause}) AND ContentHash IS NOT NULL AND EntryType = 'D'
),
RawDup AS (
    SELECT ContentHash, SizeBytes
    FROM Base
    GROUP BY ContentHash, SizeBytes
    HAVING COUNT(*) > 1
),
F AS MATERIALIZED (
    SELECT b.FullPath, b.FileName, b.ContentHash, b.SizeBytes, parent_path(b.FullPath) AS ParentPath
    FROM Base b
    JOIN RawDup r ON r.ContentHash = b.ContentHash AND r.SizeBytes = b.SizeBytes
),
Independent AS{(materialize ? " MATERIALIZED" : "")} (
    SELECT f.FullPath, f.FileName, f.ContentHash, f.SizeBytes
    FROM F f
    WHERE NOT EXISTS (
        SELECT 1 FROM F a
        WHERE a.FullPath    = f.ParentPath
          AND a.ContentHash = f.ContentHash
          AND a.SizeBytes   = f.SizeBytes
    )
)";

    /// <summary>
    /// The <see cref="IndependentFoldersCte"/> chain extended with a <c>Redundant</c> CTE that lists the
    /// duplicate folder sets wholly contained inside a <em>different</em> duplicate set — every one of
    /// their copies sits under a copy of a larger duplicate tree, so the larger set already accounts for
    /// them. The supporting CTEs are:
    /// <list type="bullet">
    /// <item><description><c>Dup</c> — the (hash, size) fingerprints that occur on more than one
    /// independent folder; i.e. the actual duplicate folder sets.</description></item>
    /// <item><description><c>DupFolders</c> — the individual independent folders belonging to those
    /// sets.</description></item>
    /// <item><description><c>Cover</c> — pairs (S-member, T-set) where the S-member folder is a strict
    /// descendant of a folder in duplicate set T. Built by climbing each member's parent-path chain
    /// (<c>Climb</c>) to the root and emitting a pair every time the current ancestor path is itself a
    /// <c>DupFolders</c> member, so <em>every</em> enclosing duplicate set is collected — O(depth) per
    /// folder rather than an O(n²) path-prefix self-join.</description></item>
    /// <item><description><c>Redundant</c> — a set S is redundant when some single set T covers
    /// <em>all</em> of S's copies (covered-member count equals S's copy count).</description></item>
    /// </list>
    /// The run-id <c>IN</c> clause is shared with the caller's command, which must have added those parameters.
    /// <para>
    /// <c>Independent</c> and <c>DupFolders</c> are <c>MATERIALIZED</c>: both are referenced several times
    /// below (and <c>DupFolders</c> twice within the <c>Cover</c> derivation, once as the climb seed and
    /// once as the ancestor lookup), and materializing <c>DupFolders</c> also lets SQLite build an
    /// automatic index on its <c>FullPath</c> for the per-step <c>Climb</c> join.
    /// (Requires SQLite ≥ 3.35, satisfied by the pinned <c>SQLitePCLRaw.bundle_e_sqlite3</c>.)
    /// </para>
    /// </summary>
    private string FolderCtesWithRedundancy(string inClause) => $@"{IndependentFoldersCte(inClause, materialize: true)},
Dup AS (
    SELECT ContentHash, SizeBytes, COUNT(*) AS Cnt
    FROM Independent
    GROUP BY ContentHash, SizeBytes
    HAVING COUNT(*) > 1
),
DupFolders AS MATERIALIZED (
    SELECT i.FullPath, i.ContentHash, i.SizeBytes, parent_path(i.FullPath) AS ParentPath
    FROM Independent i
    JOIN Dup d ON d.ContentHash = i.ContentHash AND d.SizeBytes = i.SizeBytes
),
Climb AS (
    SELECT ContentHash AS SHash, SizeBytes AS SSize, FullPath AS SPath, ParentPath AS Cur
    FROM DupFolders
    UNION ALL
    SELECT SHash, SSize, SPath, parent_path(Cur)
    FROM Climb
    WHERE Cur IS NOT NULL
),
Cover AS (
    SELECT c.SHash, c.SSize, c.SPath, a.ContentHash AS THash, a.SizeBytes AS TSize
    FROM Climb c
    JOIN DupFolders a ON a.FullPath = c.Cur
),
Redundant AS (
    SELECT covered.SHash, covered.SSize
    FROM (
        SELECT SHash, SSize, THash, TSize, COUNT(DISTINCT SPath) AS CoveredMembers
        FROM Cover
        GROUP BY SHash, SSize, THash, TSize
    ) covered
    JOIN Dup d ON d.ContentHash = covered.SHash AND d.SizeBytes = covered.SSize
    WHERE covered.CoveredMembers = d.Cnt
)";

    /// <summary>
    /// Add a <c>@run{i}</c> parameter for each scan id and return the comma-separated parameter list
    /// for use inside a <c>ScanRunId IN (...)</c> clause.
    /// </summary>
    private static string BuildRunIdInClause(SqliteCommand cmd, IReadOnlyList<string> runIds)
    {
        var names = new string[runIds.Count];
        for (var i = 0; i < runIds.Count; i++)
        {
            names[i] = "@run" + i;
            cmd.Parameters.AddWithValue("@run" + i, runIds[i]);
        }
        return string.Join(", ", names);
    }
}
