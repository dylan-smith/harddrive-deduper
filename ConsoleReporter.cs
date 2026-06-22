namespace HarddriveDeduper;

/// <summary>
/// All user-facing console output for a scan: the startup banner, the live (in-place) progress
/// line, the final summary, and fatal-error reporting.
/// </summary>
public sealed class ConsoleReporter
{
    private readonly Options _options;

    public ConsoleReporter(Options options) => _options = options;

    /// <summary>Print what we're about to do: which drives, the hashing mode, and the destination.</summary>
    public void PrintBanner(IReadOnlyList<string> roots)
    {
        Console.WriteLine($"Scanning {roots.Count} drive(s): {string.Join(", ", roots)}");
        Console.WriteLine($"Hashing: {(_options.ComputeHash ? $"SHA-256 ({_options.Parallelism} threads)" : "disabled")}");
        Console.WriteLine($"Database: {Redact(_options.ConnectionString)}  ->  {_options.TableName}");
        Console.WriteLine();
    }

    /// <summary>
    /// Start a live progress display with one block of lines per drive (a header line plus one line
    /// per pass), all repainted together every second so several drives scanning in parallel each show
    /// their own progress at the same time. Wire each returned <see cref="MultiProgress.Slot"/> to its
    /// drive's passes; dispose (await) the display to stop repainting and leave the final values shown.
    /// </summary>
    public MultiProgress StartMultiProgress(IReadOnlyList<string> drives, bool hasHashPass, bool hasFolderPass)
    {
        var slots = new MultiProgress.Slot[drives.Count];
        for (int i = 0; i < drives.Count; i++)
            slots[i] = new MultiProgress.Slot($"[{i + 1}/{drives.Count}] {drives[i]}", hasHashPass, hasFolderPass);
        return new MultiProgress(slots);
    }

    /// <summary>
    /// Render a fixed-width text progress bar like <c>[#########-----------]  45%</c> for a pass whose
    /// total is known up front. A non-positive <paramref name="total"/> renders as complete.
    /// </summary>
    public static string ProgressBar(long done, long total, int width = 20)
    {
        double fraction = total <= 0 ? 1.0 : Math.Clamp((double)done / total, 0.0, 1.0);
        int filled = (int)Math.Round(fraction * width);
        return $"[{new string('#', filled)}{new string('-', width - filled)}] {fraction * 100,3:0}%";
    }

    /// <summary>Print the final, aggregated tally once every drive has finished (or been canceled).</summary>
    public void PrintSummary(ScanTotals totals, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("Done.");
        Console.WriteLine($"  Files seen:       {totals.FilesSeen:N0}");
        Console.WriteLine($"  Rows written:     {totals.RowsWritten:N0}");
        Console.WriteLine($"  Files hashed:     {totals.FilesHashed:N0}");
        if (totals.FoldersWritten > 0)
            Console.WriteLine($"  Folders fingerprinted: {totals.FoldersWritten:N0}");
        string skipNote = totals.DirectoriesSkipped > 0 ? $"  (logged to {_options.SkipTableName})" : "";
        Console.WriteLine($"  Directories skipped (access): {totals.DirectoriesSkipped:N0}{skipNote}");
        Console.WriteLine($"  Hash errors:      {totals.HashErrors:N0}");
        Console.WriteLine($"  Elapsed:          {elapsed:hh\\:mm\\:ss}");
    }

    /// <summary>Print the ranked duplicate sets produced by <see cref="DuplicateAnalyzer"/>.</summary>
    public void PrintDuplicates(DuplicateAnalysis analysis, int topN)
    {
        if (analysis.Scans.Count == 0)
        {
            Console.WriteLine($"No completed scan data found in {_options.TableName}. Run a scan first.");
            return;
        }

        if (analysis.Scans.Count == 1)
        {
            ScanRef s = analysis.Scans[0];
            Console.WriteLine($"Analyzing latest scan for {s.Drive} — scan {s.ScanRunId} (scanned {s.CompletedAtUtc:yyyy-MM-dd HH:mm} UTC).");
        }
        else
        {
            Console.WriteLine($"Analyzing the latest completed scan of {analysis.Scans.Count} drive(s), combined:");
            foreach (ScanRef s in analysis.Scans)
                Console.WriteLine($"  {s.Drive,-10} scan {s.ScanRunId} (scanned {s.CompletedAtUtc:yyyy-MM-dd HH:mm} UTC)");
        }
        Console.WriteLine();

        IReadOnlyList<DuplicateGroup> groups = analysis.Groups;
        if (groups.Count == 0 && analysis.FolderGroups.Count == 0)
        {
            Console.WriteLine("No duplicates found — no hashed content appears at more than one location.");
            return;
        }

        if (groups.Count > 0)
        {
            Console.WriteLine($"Total wasted space across all duplicate file set(s): {FormatBytes(analysis.TotalWastedBytes)}");
            Console.WriteLine();
            Console.WriteLine($"Top {Math.Min(topN, groups.Count)} duplicate file set(s) by wasted space (redundant copies × size):");
            Console.WriteLine();
            PrintGroups(groups, "<filenames differ>");
        }

        // Duplicate folders are the same redundant files seen at directory granularity — deleting one
        // whole redundant tree reclaims the space in a single step. Their bytes overlap the file totals
        // above, so they are reported separately rather than added in.
        if (analysis.FolderGroups.Count > 0)
        {
            Console.WriteLine($"Top {Math.Min(topN, analysis.FolderGroups.Count)} duplicate folder(s) — identical directory trees");
            Console.WriteLine("(wasted space overlaps the file totals above; delete a redundant tree to reclaim it):");
            Console.WriteLine();
            PrintGroups(analysis.FolderGroups, "<folder names differ>");
        }
    }

    /// <summary>Print one ranked list of duplicate sets (files or folders) with their sample locations.</summary>
    private static void PrintGroups(IReadOnlyList<DuplicateGroup> groups, string variedNameLabel)
    {
        int rank = 1;
        foreach (DuplicateGroup g in groups)
        {
            string name = g.DistinctNameCount == 1 ? g.FileName : variedNameLabel;
            Console.WriteLine(
                $"#{rank,-2} {FormatBytes(g.WastedBytes),11} wasted  |  {name}  |  " +
                $"{g.CopyCount} copies × {FormatBytes(g.SizeBytes)}  |  hash {g.ContentHash[..12]}…");

            foreach (string path in g.SamplePaths)
                Console.WriteLine($"       {path}");
            if (g.CopyCount > g.SamplePaths.Count)
                Console.WriteLine($"       … and {g.CopyCount - g.SamplePaths.Count} more location(s)");

            Console.WriteLine();
            rank++;
        }
    }

    public void ReportFatalError(string message) =>
        Console.Error.WriteLine("\nFatal error during scan: " + message);

    /// <summary>Render a byte count as a human-friendly size (e.g. "1.50 GB").</summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.##} {units[unit]}";
    }

    /// <summary>Hide any password in the connection string before echoing it to the console.</summary>
    private static string Redact(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string key = parts[i].Split('=', 2)[0].Trim().ToLowerInvariant();
            if (key is "password" or "pwd")
                parts[i] = parts[i].Split('=', 2)[0] + "=***";
        }
        return string.Join(';', parts);
    }
}
