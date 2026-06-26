using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DupeHunter;

/// <summary>
/// Walks a directory tree manually (so a single inaccessible folder doesn't abort the whole
/// enumeration) and produces a <see cref="FileRecord"/> per file, optionally hashing contents.
/// </summary>
internal sealed class FileScanner(Options options)
{
    /// <summary>Read buffer used while streaming a file through the hash function.</summary>
    private const int HashBufferBytes = 1 << 20; // 1 MB

    private readonly Options _options = options;

    public long FilesSeen;
    public long BytesSeen;
    public long FilesHashed;
    public long BytesHashed;
    public long DirectoriesSkipped;
    public long HashErrors;

    /// <summary>Directories that couldn't be enumerated, with the reason. Populated as the scan runs.</summary>
    public readonly ConcurrentQueue<SkipRecord> Skips = new();

    /// <summary>Lazily enumerate file metadata under <paramref name="root"/>. Hashing happens later.</summary>
    public IEnumerable<FileRecord> EnumerateFiles(string root)
    {
        // Explicit stack instead of recursion: avoids deep call stacks on long path chains and
        // lets us swallow access errors per-directory.
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            // Queue sub-directories first.
            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                RecordSkip(dir, ex);
                continue;
            }

            foreach (var sub in subDirs)
            {
                if (!_options.FollowReparsePoints && IsReparsePoint(sub))
                {
                    continue;
                }

                pending.Push(sub);
            }

            // Then files in this directory.
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                RecordSkip(dir, ex);
                continue;
            }

            foreach (var path in files)
            {
                var record = TryReadMetadata(path);
                if (record is not null)
                {
                    Interlocked.Increment(ref FilesSeen);
                    Interlocked.Add(ref BytesSeen, record.SizeBytes);
                    yield return record;
                }
            }
        }
    }

    /// <summary>Count an inaccessible directory and remember its path + reason for later persistence.</summary>
    private void RecordSkip(string dir, Exception ex)
    {
        Interlocked.Increment(ref DirectoriesSkipped);
        Skips.Enqueue(new SkipRecord { FullPath = dir, Reason = ex.GetType().Name + ": " + ex.Message });
    }

    private static FileRecord? TryReadMetadata(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // Skip reparse-point files (symlinks) — the target is recorded elsewhere if reachable.
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? null
                : new FileRecord
                {
                    FileName = info.Name,
                    FullPath = info.FullName,
                    SizeBytes = info.Length,
                    DateModifiedUtc = info.LastWriteTimeUtc,
                    DateCreatedUtc = info.CreationTimeUtc,
                };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pass two: compute the SHA-256 of one file's contents, streaming so memory stays flat on huge
    /// files. Files larger than <see cref="Options.MaxHashBytes"/> are intentionally left unhashed
    /// (a result with no hash and no error), and their metadata row keeps its NULL hash.
    /// </summary>
    public HashResult HashFile(PendingHash pending)
    {
        if (_options.MaxHashBytes > 0 && pending.SizeBytes > _options.MaxHashBytes)
        {
            return new HashResult(pending.Id, ContentHash: null, Error: null);
        }

        try
        {
            using var stream = new FileStream(
                pending.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, // tolerate files held open by other processes
                bufferSize: HashBufferBytes,
                FileOptions.SequentialScan);

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            Interlocked.Increment(ref FilesHashed);
            Interlocked.Add(ref BytesHashed, pending.SizeBytes);
            return new HashResult(pending.Id, Convert.ToHexStringLower(hash), Error: null);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref HashErrors);
            return new HashResult(pending.Id, ContentHash: null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool IsReparsePoint(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }
}
