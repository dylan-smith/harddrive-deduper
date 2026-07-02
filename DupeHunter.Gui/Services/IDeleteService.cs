namespace DupeHunter.Gui.Services;

public enum DeleteStatus
{
    /// <summary>Removed from disk and from the database.</summary>
    Deleted,

    /// <summary>Wasn't on disk to begin with (the database row, if any, was left alone).</summary>
    AlreadyMissing,

    /// <summary>In use by another process; nothing was changed.</summary>
    Locked,

    /// <summary>Deletion failed (permissions, hardware, …); nothing was changed.</summary>
    Failed,
}

public sealed record DeleteOutcome(DeleteStatus Status, string? Error = null);

/// <summary>
/// Permanently deletes a duplicate copy from disk and keeps the database consistent afterwards.
/// There is no Recycle Bin step — a successful delete is unrecoverable, so callers must confirm
/// with the user first.
/// </summary>
public interface IDeleteService
{
    /// <summary>
    /// Permanently delete the file (or folder tree) from disk, then remove its database rows.
    /// Never throws for the expected failure modes — they come back as the outcome's status.
    /// </summary>
    Task<DeleteOutcome> DeleteAsync(string fullPath, bool isFolder, CancellationToken ct);

    /// <summary>
    /// Remove the database rows for an entry that is already gone from disk (a stale record from
    /// an old scan). Returns the number of rows removed.
    /// </summary>
    Task<int> RemoveStaleRowsAsync(string fullPath, bool isFolder, CancellationToken ct);
}
