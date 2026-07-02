namespace DupeHunter.Gui.Services;

public enum DeleteStatus
{
    /// <summary>Removed from disk.</summary>
    Deleted,

    /// <summary>Wasn't on disk to begin with.</summary>
    AlreadyMissing,

    /// <summary>In use by another process; nothing was changed.</summary>
    Locked,

    /// <summary>Deletion failed (permissions, hardware, …); nothing was changed.</summary>
    Failed,
}

public sealed record DeleteOutcome(DeleteStatus Status, string? Error = null);

/// <summary>
/// Permanently deletes a duplicate copy from disk. Disk only — reflecting the deletion in the YAML
/// report is the caller's job (see <see cref="DuplicateReport"/>). There is no Recycle Bin step — a
/// successful delete is unrecoverable, so callers must confirm with the user first.
/// </summary>
public interface IDeleteService
{
    /// <summary>
    /// Permanently delete the file (or folder tree) from disk. Never throws for the expected failure
    /// modes — they come back as the outcome's status.
    /// </summary>
    Task<DeleteOutcome> DeleteAsync(string fullPath, bool isFolder, CancellationToken ct);
}
