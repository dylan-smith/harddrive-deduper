using System.Collections.Concurrent;

namespace HarddriveDeduper;

/// <summary>Aggregated counters across every drive scanned this process, for the final summary.</summary>
public sealed record ScanTotals(
    long FilesSeen,
    long RowsWritten,
    long FilesHashed,
    long FoldersWritten,
    long DirectoriesSkipped,
    long HashErrors);

/// <summary>
/// Runs the scan in two passes per drive, each drive being its own scan run with a distinct
/// ScanRunId. Pass one enumerates every file and writes its metadata (with no content hash). Pass
/// two reads those rows back and fills in their content hashes. Committing the full metadata
/// inventory before any hashing means an interrupted hashing pass still leaves a complete file
/// listing behind. Drives are scanned concurrently — one scanner and one database writer (hence one
/// connection) per drive, so they stay fully isolated — and each drive shows its own live progress.
/// Returns a process exit code (0 = success, 130 = canceled, 1 = fatal error).
/// </summary>
public sealed class ScanPipeline
{
    private readonly Options _options;
    private readonly ConsoleReporter _reporter;

    public ScanPipeline(Options options, ConsoleReporter reporter)
    {
        _options = options;
        _reporter = reporter;
    }

    /// <summary>
    /// Scan every drive in parallel. <paramref name="primaryWriter"/> is an already-initialized writer
    /// (its <see cref="DatabaseWriter.InitializeAsync"/> created the schema) reused for the first drive;
    /// the remaining drives get their own writers, created and disposed here. Returns the worst exit
    /// code across drives together with the aggregated counters for the summary.
    /// </summary>
    public async Task<(int ExitCode, ScanTotals Totals)> RunAsync(
        IReadOnlyList<string> roots, DatabaseWriter primaryWriter, CancellationToken ct)
    {
        var scanners = new FileScanner[roots.Count];
        var writers = new DatabaseWriter[roots.Count];
        var ownedWriters = new List<DatabaseWriter>(); // writers created here, to dispose at the end

        // If one drive fails fatally, cancel the rest rather than leaving them running uselessly.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            for (int i = 0; i < roots.Count; i++)
            {
                scanners[i] = new FileScanner(_options);
                if (i == 0)
                {
                    writers[i] = primaryWriter; // already initialized and open
                }
                else
                {
                    var w = new DatabaseWriter(_options);
                    await w.OpenConnectionAsync(ct);
                    writers[i] = w;
                    ownedWriters.Add(w);
                }
            }

            int[] codes;
            bool hasFolderPass = _options.ComputeHash && _options.ComputeFolderFingerprints;
            await using (MultiProgress display = _reporter.StartMultiProgress(roots, _options.ComputeHash, hasFolderPass))
            {
                var tasks = new Task<int>[roots.Count];
                for (int i = 0; i < roots.Count; i++)
                {
                    int idx = i; // capture per-iteration
                    tasks[idx] = ScanDriveAsync(roots[idx], scanners[idx], writers[idx], display.Slots[idx], linked);
                }

                codes = await Task.WhenAll(tasks);
            }

            var totals = new ScanTotals(
                FilesSeen: scanners.Sum(s => s.FilesSeen),
                RowsWritten: writers.Sum(w => w.RowsWritten),
                FilesHashed: scanners.Sum(s => s.FilesHashed),
                FoldersWritten: writers.Sum(w => w.FoldersWritten),
                DirectoriesSkipped: scanners.Sum(s => s.DirectoriesSkipped),
                HashErrors: scanners.Sum(s => s.HashErrors));

            // Surface the most severe outcome: a fatal failure outranks a cancellation outranks success.
            int exitCode = codes.Contains(1) ? 1 : codes.Contains(130) ? 130 : 0;
            return (exitCode, totals);
        }
        finally
        {
            foreach (DatabaseWriter w in ownedWriters)
                w.Dispose();
        }
    }

    /// <summary>Scan a single drive root as its own scan run, returning a process exit code.</summary>
    private async Task<int> ScanDriveAsync(
        string root, FileScanner scanner, DatabaseWriter writer, MultiProgress.Slot slot, CancellationTokenSource linked)
    {
        CancellationToken ct = linked.Token;

        // Log this drive's run as started; it stays "Running" with no completion time until we stamp it below.
        await writer.BeginScanAsync(root, ct);

        try
        {
            // Pass one: enumerate and persist metadata for every file.
            await EnumerateMetadataAsync(root, scanner, writer, slot, ct);

            // Pass two: read the rows back and fill in their content hashes.
            if (_options.ComputeHash)
                await ComputeHashesAsync(scanner, writer, slot, ct);

            // Pass three: fingerprint every folder from the hashes just written (no files are re-read).
            if (_options.ComputeHash && _options.ComputeFolderFingerprints)
                await ComputeFolderFingerprintsAsync(writer, slot, ct);

            await writer.WriteSkipsAsync(DrainSkips(scanner), CancellationToken.None);
            await writer.CompleteScanAsync("Completed", null, CancellationToken.None);
            return 0;
        }
        catch (OperationCanceledException)
        {
            // Flush whatever metadata was already buffered before exiting.
            try { await writer.FlushAsync(CancellationToken.None); } catch { /* ignore */ }
            try { await writer.WriteSkipsAsync(DrainSkips(scanner), CancellationToken.None); } catch { /* ignore */ }
            // Record the partial run as canceled; its rows must not be treated as a complete inventory.
            try { await writer.CompleteScanAsync("Canceled", null, CancellationToken.None); } catch { /* ignore */ }
            return 130;
        }
        catch (Exception ex)
        {
            _reporter.ReportFatalError(ex.Message);
            // Stop the sibling drives — this run can't produce a clean result anyway.
            linked.Cancel();
            try { await writer.FlushAsync(CancellationToken.None); } catch { /* ignore */ }
            try { await writer.CompleteScanAsync("Failed", ex.Message, CancellationToken.None); } catch { /* ignore */ }
            return 1;
        }
    }

    /// <summary>
    /// Pass one: walk the tree and bulk-insert each file's metadata. Enumeration is single-threaded
    /// (it's directory I/O, not CPU work) and <see cref="DatabaseWriter.AddAsync"/> flushes in batches.
    /// </summary>
    private async Task EnumerateMetadataAsync(
        string root, FileScanner scanner, DatabaseWriter writer, MultiProgress.Slot slot, CancellationToken ct)
    {
        // Scanner and writer are this drive's alone, so their counters already read as per-drive totals.
        slot.StartEnumerate(() =>
            $"files: {scanner.FilesSeen:N0}  written: {writer.RowsWritten:N0}  " +
            $"dirs skipped: {scanner.DirectoriesSkipped:N0}");

        foreach (FileRecord record in scanner.EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            await writer.AddAsync(record, ct);
        }

        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// Pass two: page through this run's rows (by Id) and hash each file on N threads, writing the
    /// results back in batches. Each chunk is read fully, hashed, then updated before the next chunk
    /// is read, so reads and updates never contend on the connection and memory stays bounded.
    /// </summary>
    private async Task ComputeHashesAsync(
        FileScanner scanner, DatabaseWriter writer, MultiProgress.Slot slot, CancellationToken ct)
    {
        await writer.BeginHashPassAsync(ct);

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.Parallelism,
            CancellationToken = ct,
        };

        // Pass one wrote exactly the rows pass two will page through, so its count is the bar's total.
        // processed advances by whole chunks; the render timer reads it for the bar as it grows.
        long total = writer.RowsWrittenThisScan;
        long processed = 0;

        slot.StartHash(() =>
            $"{ConsoleReporter.ProgressBar(processed, total)}  " +
            $"hashed: {scanner.FilesHashed:N0}  hash errors: {scanner.HashErrors:N0}");

        long afterId = 0;
        while (true)
        {
            IReadOnlyList<PendingHash> chunk = await writer.ReadNextHashChunkAsync(afterId, ct);
            if (chunk.Count == 0)
                break;

            // Rows come back ordered by Id, so the last one is this chunk's high-water mark.
            afterId = chunk[^1].Id;

            // Hash the chunk in parallel; keep only rows that actually got a hash or hit an error
            // (files skipped for exceeding the size limit are left with their NULL hash).
            var updates = new ConcurrentQueue<HashResult>();
            await Parallel.ForEachAsync(chunk, parallelOpts, (pending, _) =>
            {
                HashResult result = scanner.HashFile(pending);
                if (result.ContentHash is not null || result.Error is not null)
                    updates.Enqueue(result);
                return ValueTask.CompletedTask;
            });

            await writer.UpdateHashesAsync(updates, ct);
            processed += chunk.Count;
        }
    }

    /// <summary>
    /// Pass three: build a content fingerprint for every folder in the tree from the file hashes already
    /// in the database — so no file is read a second time. Streams this run's file rows ordered by hash
    /// into a <see cref="FolderFingerprinter"/>, then bulk-inserts one folder row per folder.
    /// </summary>
    private static async Task ComputeFolderFingerprintsAsync(
        DatabaseWriter writer, MultiProgress.Slot slot, CancellationToken ct)
    {
        var fingerprinter = new FolderFingerprinter();
        slot.StartFolders(() => $"folders: {fingerprinter.FolderCount:N0}");

        await writer.StreamHashedFilesAsync(file => fingerprinter.Add(file), ct);

        var folders = fingerprinter.Finish().ToArray();
        await writer.WriteFoldersAsync(folders, ct);
    }

    /// <summary>
    /// Remove and return the directory skips this drive's scanner accumulated. Called once per drive
    /// after its enumeration finishes, so the returned skips belong to that drive and are tagged with
    /// its ScanRunId by the writer.
    /// </summary>
    private static SkipRecord[] DrainSkips(FileScanner scanner)
    {
        var skips = new List<SkipRecord>();
        while (scanner.Skips.TryDequeue(out SkipRecord? skip))
            skips.Add(skip);
        return skips.ToArray();
    }
}
