namespace HarddriveDeduper;

/// <summary>
/// A single file's metadata, captured during pass one and inserted with no content hash.
/// Hashes are computed and filled in separately during pass two.
/// </summary>
public sealed class FileRecord
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime DateModifiedUtc { get; init; }
    public required DateTime DateCreatedUtc { get; init; }
}

/// <summary>
/// A row that pass two needs to hash: the table identity plus the location and size needed to
/// re-open the file and decide whether it's within the hashing size limit. Read back from the
/// database after pass one has committed the metadata.
/// </summary>
public readonly record struct PendingHash(long Id, string FullPath, long SizeBytes);

/// <summary>The outcome of hashing one file in pass two, keyed back to its table row by <see cref="Id"/>.</summary>
public readonly record struct HashResult(long Id, string? ContentHash, string? Error);

/// <summary>
/// One file's hash plus the fields the folder-fingerprint pass aggregates. Read back from the
/// database (ordered by content hash) after pass two has filled in the hashes; <see cref="ContentHash"/>
/// is null for files that were left unhashed (too large, or a hashing error), which taint their folders.
/// </summary>
public readonly record struct HashedFile(
    string FullPath, string? ContentHash, long SizeBytes, DateTime DateModifiedUtc, DateTime DateCreatedUtc);

/// <summary>
/// A folder row produced by the fingerprint pass: the folder's path, its content fingerprint (a hash of
/// all descendant file hashes, or null if any descendant was unhashed), and aggregates over its subtree.
/// Inserted into the same table as files, distinguished by an <c>EntryType</c> of <c>'D'</c>.
/// </summary>
public readonly record struct FolderRecord(
    string FullPath, string FileName, string? ContentHash, long SizeBytes,
    DateTime DateModifiedUtc, DateTime DateCreatedUtc);
