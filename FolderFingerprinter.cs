using System.Security.Cryptography;

namespace HarddriveDeduper;

/// <summary>
/// Builds a content fingerprint for every folder in a scanned tree from the file hashes already in the
/// database — so files are never re-read. A folder's fingerprint is a hash of the multiset of <em>every</em>
/// descendant file's content hash, independent of file/folder names and of how the files are arranged in
/// subfolders: two folders match exactly when they hold the same set of file contents anywhere underneath.
/// </summary>
/// <remarks>
/// Feed every file via <see cref="Add"/> in <strong>content-hash order</strong> (the database query orders
/// the rows), then call <see cref="Finish"/>. Each file is folded into every one of its ancestor folders up
/// to the drive root, so the parent of <c>C:\a\b\f.txt</c> contributes to <c>C:\a\b</c>, <c>C:\a</c> and
/// <c>C:\</c>. Because the files arrive in hash order, each folder accumulates its descendant hashes in
/// sorted order, making the fingerprint deterministic regardless of enumeration order.
///
/// If any descendant file has no hash (it exceeded the size limit or hit a hashing error), the folder — and
/// every ancestor above it — is marked tainted and gets a null fingerprint, so a folder is never claimed
/// equal to another unless every file beneath it was actually hashed. Memory scales with the number of
/// folders (a small incremental hasher and a few counters each), not with the number of files.
/// </remarks>
public sealed class FolderFingerprinter
{
    /// <summary>Running state for one folder while files are folded in; finalized by <see cref="Finish"/>.</summary>
    private sealed class Accumulator
    {
        public readonly IncrementalHash Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long FileCount;
        public long SizeBytes;
        public DateTime MaxModifiedUtc = DateTime.MinValue;
        public DateTime MinCreatedUtc = DateTime.MaxValue;
        public bool Tainted;
    }

    // Folder paths come straight from the filesystem with consistent casing within a scan, so an ordinal
    // map keys each real directory exactly once.
    private readonly Dictionary<string, Accumulator> _folders = new(StringComparer.Ordinal);

    /// <summary>Number of distinct folders discovered so far.</summary>
    public int FolderCount => _folders.Count;

    /// <summary>
    /// Fold one file into every ancestor folder up to (and including) the drive root. Files must be added
    /// in content-hash order so each folder's hashes accumulate in sorted order.
    /// </summary>
    public void Add(in HashedFile file)
    {
        byte[]? hashBytes = file.ContentHash is { } hex ? Convert.FromHexString(hex) : null;

        for (string? dir = Path.GetDirectoryName(file.FullPath); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (!_folders.TryGetValue(dir, out Accumulator? acc))
                _folders[dir] = acc = new Accumulator();

            acc.FileCount++;
            acc.SizeBytes += file.SizeBytes;
            if (file.DateModifiedUtc > acc.MaxModifiedUtc) acc.MaxModifiedUtc = file.DateModifiedUtc;
            if (file.DateCreatedUtc < acc.MinCreatedUtc) acc.MinCreatedUtc = file.DateCreatedUtc;

            if (hashBytes is null)
                acc.Tainted = true;
            else
                acc.Hasher.AppendData(hashBytes);
        }
    }

    /// <summary>
    /// Finalize and return a row per folder. A non-tainted folder's fingerprint is the SHA-256 of its
    /// accumulated descendant hashes with the descendant count folded in (so one copy of a hash differs
    /// from two); a tainted folder gets a null fingerprint but still reports its aggregated size/dates.
    /// Disposes each folder's hasher as it goes; the instance should not be reused afterward.
    /// </summary>
    public IEnumerable<FolderRecord> Finish()
    {
        foreach ((string path, Accumulator acc) in _folders)
        {
            string? fingerprint = null;
            if (!acc.Tainted)
            {
                acc.Hasher.AppendData(BitConverter.GetBytes(acc.FileCount));
                fingerprint = Convert.ToHexStringLower(acc.Hasher.GetHashAndReset());
            }
            acc.Hasher.Dispose();

            yield return new FolderRecord(
                FullPath: path,
                FileName: FolderName(path),
                ContentHash: fingerprint,
                SizeBytes: acc.SizeBytes,
                DateModifiedUtc: acc.MaxModifiedUtc,
                DateCreatedUtc: acc.MinCreatedUtc);
        }
    }

    /// <summary>The folder's leaf name, falling back to the whole path for a drive root (e.g. <c>C:\</c>).</summary>
    private static string FolderName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
