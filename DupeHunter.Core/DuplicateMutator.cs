using Microsoft.Data.Sqlite;

namespace DupeHunter;

/// <summary>
/// Keeps the database consistent after a duplicate copy is deleted from disk: removes the deleted
/// entry's rows and invalidates the folder fingerprints its removal made stale. All changes are
/// scoped to the same runs the analysis reads (the latest completed scan per drive), and each call
/// commits in a single transaction so the database never half-reflects a deletion. The
/// <c>Scans</c> audit log and <c>ScanSkips</c> are never touched.
/// </summary>
public sealed class DuplicateMutator
{
    private readonly Options _options;
    private readonly DuplicateAnalyzer _analyzer;

    public DuplicateMutator(Options options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _analyzer = new DuplicateAnalyzer(options);
    }

    /// <summary>
    /// Create the <c>FullPath</c> index the removal statements rely on, if it doesn't exist yet. The
    /// scanner never needed one, but the subtree range-delete and ancestor updates would otherwise
    /// scan the whole table per deletion. Idempotent; run once when opening a database for editing
    /// (the first run on a large database can take a while — report it via <paramref name="progress"/>).
    /// </summary>
    public async Task EnsurePathIndexAsync(CancellationToken ct, IStepProgress? progress = null)
    {
        await using var conn = await Database.OpenConnectionAsync(_options, ct);
        progress?.BeginStep("Indexing file paths…");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS IX_{_options.TableName}_FullPath ON {_options.TableName}(FullPath);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Remove a deleted file's row from the analyzed runs and invalidate every ancestor folder's
    /// fingerprint (see <see cref="TaintAncestorsAsync"/>). Returns the number of rows deleted —
    /// 0 means the path wasn't in the analyzed runs (e.g. already removed).
    /// </summary>
    public async Task<int> RemoveFileRowAsync(string fullPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        await using var conn = await Database.OpenConnectionAsync(_options, ct);
        var runIds = await GetActiveRunIdsAsync(conn, ct);
        if (runIds.Count == 0)
        {
            return 0;
        }

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
DELETE FROM {_options.TableName}
WHERE EntryType = 'F' AND FullPath = @path AND ScanRunId IN ({inClause});";
        cmd.Parameters.AddWithValue("@path", fullPath);
        var deleted = await cmd.ExecuteNonQueryAsync(ct);

        if (deleted > 0)
        {
            await TaintAncestorsAsync(conn, tx, runIds, fullPath, ct);
        }

        await tx.CommitAsync(ct);
        return deleted;
    }

    /// <summary>
    /// Remove a deleted folder's fingerprint row and every descendant row (files and subfolder
    /// fingerprints) from the analyzed runs, and invalidate every ancestor folder's fingerprint.
    /// Returns the number of rows deleted — 0 means the subtree wasn't in the analyzed runs.
    /// </summary>
    public async Task<int> RemoveFolderSubtreeRowsAsync(string folderPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderPath);
        folderPath = folderPath.TrimEnd('\\'); // stored folder paths never carry a trailing separator

        await using var conn = await Database.OpenConnectionAsync(_options, ct);
        var runIds = await GetActiveRunIdsAsync(conn, ct);
        if (runIds.Count == 0)
        {
            return 0;
        }

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // Descendants are matched with a binary-collation range rather than LIKE: paths may contain
        // LIKE wildcards (% and _), LIKE is case-insensitive for ASCII by default, and a range scan
        // can use the FullPath index. ']' is the code point right after '\' (0x5D vs 0x5C), so
        // (path + '\', path + ']') brackets exactly the strings that extend path with a separator.
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var inClause = BuildRunIdInClause(cmd, runIds);
        cmd.CommandText = $@"
DELETE FROM {_options.TableName}
WHERE ScanRunId IN ({inClause})
  AND (FullPath = @path OR (FullPath > @path || '\' AND FullPath < @path || ']'));";
        cmd.Parameters.AddWithValue("@path", folderPath);
        var deleted = await cmd.ExecuteNonQueryAsync(ct);

        if (deleted > 0)
        {
            await TaintAncestorsAsync(conn, tx, runIds, folderPath, ct);
        }

        await tx.CommitAsync(ct);
        return deleted;
    }

    /// <summary>
    /// NULL out the fingerprint of every folder on the deleted path's ancestor chain: their subtrees
    /// just changed, so their stored fingerprints no longer describe what's on disk. A NULL
    /// <c>ContentHash</c> is the same "tainted" convention <c>FolderFingerprinter</c> uses for
    /// unhashable folders, and every analysis query already excludes such rows, so the stale folders
    /// simply drop out of folder-duplicate results until the next scan recomputes them.
    /// The chain is computed with <see cref="Path.GetDirectoryName(string)"/>, which matches the
    /// stored <c>FullPath</c> values byte-for-byte (the <c>parent_path</c> contract in <see cref="Database"/>).
    /// </summary>
    private async Task TaintAncestorsAsync(
        SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<string> runIds, string deletedPath, CancellationToken ct)
    {
        var ancestors = new List<string>();
        for (var p = Path.GetDirectoryName(deletedPath); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            ancestors.Add(p);
        }

        if (ancestors.Count == 0)
        {
            return;
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var inClause = BuildRunIdInClause(cmd, runIds);
        var names = new string[ancestors.Count];
        for (var i = 0; i < ancestors.Count; i++)
        {
            names[i] = "@a" + i;
            cmd.Parameters.AddWithValue("@a" + i, ancestors[i]);
        }

        cmd.CommandText = $@"
UPDATE {_options.TableName} SET ContentHash = NULL
WHERE EntryType = 'D' AND ScanRunId IN ({inClause}) AND FullPath IN ({string.Join(", ", names)});";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The run ids the analysis reads — the latest completed scan per drive.</summary>
    private async Task<List<string>> GetActiveRunIdsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var scans = await _analyzer.GetLatestCompletedScansAsync(conn, ct);
        return scans.Select(s => s.ScanRunId).ToList();
    }

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
