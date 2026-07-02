using Microsoft.Data.Sqlite;

namespace DupeHunter;

/// <summary>
/// Opens connections to the SQLite database file, applying the pragmas every connection needs. The
/// whole tool shares one <c>.db</c> file: WAL mode lets the parallel drive scans read concurrently
/// while one writer commits, and writes are additionally serialized at the app level (see the shared
/// write lock in <see cref="DatabaseWriter"/>) so concurrent writers never collide.
/// </summary>
public static class Database
{
    /// <summary>How long a blocked statement waits for a lock before failing — a safety net behind the
    /// app-level write lock (e.g. a read briefly contending with the WAL checkpoint).</summary>
    private const int BusyTimeoutMs = 30_000;

    /// <summary>Create and open a connection to the configured database file with the standard pragmas.</summary>
    public static async Task<SqliteConnection> OpenConnectionAsync(Options options, CancellationToken ct)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(ct);
        await ConfigureAsync(connection, ct);

        // The folder-duplicate analysis navigates the folder tree by parent path rather than by
        // expensive path-prefix self-joins. parent_path mirrors how FolderFingerprinter keyed each
        // folder (Path.GetDirectoryName up to the drive root, returning NULL at a root / UNC share),
        // so the values match the stored FullPath byte-for-byte. Deterministic so SQLite can hoist it.
        connection.CreateFunction(
            "parent_path",
            (string? p) => p is null ? null : Path.GetDirectoryName(p),
            isDeterministic: true);

        return connection;
    }

    /// <summary>
    /// Apply the per-connection pragmas. WAL is persisted in the database file once set, but
    /// <c>synchronous</c> and <c>busy_timeout</c> are per-connection, so this runs on every connection.
    /// <c>synchronous=NORMAL</c> is safe under WAL and much faster; a file index is cheaply rebuilt, so
    /// the tiny power-loss durability trade-off is acceptable.
    /// </summary>
    public static async Task ConfigureAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = {BusyTimeoutMs};";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
