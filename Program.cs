using System.Diagnostics;
using HarddriveDeduper;

// Parse the command line first; bail out early on bad input or a help request.
Options options;
try
{
    options = Options.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(Options.HelpText());
    return 0;
}

// Ctrl-C => graceful cancellation for whichever mode we run.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nCancellation requested...");
    cts.Cancel();
};

// Analysis mode reads an already-scanned table and exits; it never touches the drives.
if (options.Analyze)
{
    var analysisReporter = new ConsoleReporter(options);
    try
    {
        var analyzer = new DuplicateAnalyzer(options);
        DuplicateAnalysis analysis =
            await analyzer.FindTopDuplicatesAsync(options.TopN, samplePathsPerGroup: 5, cts.Token);
        analysisReporter.PrintDuplicates(analysis, options.TopN);
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Analysis canceled.");
        return 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Analysis failed:");
        Console.Error.WriteLine("  " + ex.Message);
        return 3;
    }
}

// Cleanup mode prunes the database (keep the latest completed scan per drive) and exits without
// touching the drives. Assumes no scan is currently running.
if (options.Cleanup)
{
    try
    {
        var cleaner = new DatabaseCleaner(options);
        Console.WriteLine("Determining retained runs (latest completed scan per drive)...");
        CleanupPlan plan = await cleaner.PlanAsync(cts.Token);

        Console.WriteLine($"Retaining {plan.KeptScans.Count} run(s):");
        foreach (ScanRef s in plan.KeptScans)
            Console.WriteLine($"  {s.Drive,-6} {s.ScanRunId}  completed {s.CompletedAtUtc:u}");
        Console.WriteLine($"Rows to delete: {plan.FilesToDelete:n0} file(s), {plan.SkipsToDelete:n0} skip(s).");
        Console.WriteLine("The scan audit log is preserved.");

        if (plan.FilesToDelete == 0 && plan.SkipsToDelete == 0)
        {
            Console.WriteLine("Nothing to delete; database is already clean.");
            return 0;
        }

        if (options.DryRun)
        {
            Console.WriteLine("Dry run: no rows were deleted.");
            return 0;
        }

        var progress = new Progress<long>(n => Console.Write($"\rDeleting... {n:n0} rows removed"));
        CleanupResult result = await cleaner.ExecuteAsync(plan, progress, cts.Token);
        Console.WriteLine();
        Console.WriteLine($"Deleted {result.FilesDeleted:n0} file row(s) and {result.SkipsDeleted:n0} skip row(s).");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\nCleanup canceled.");
        return 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Cleanup failed:");
        Console.Error.WriteLine("  " + ex.Message);
        return 3;
    }
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Warning: this tool targets Windows drive semantics; behavior on other platforms is best-effort.");
}

List<string> roots = DriveResolver.ResolveRoots(options);
if (roots.Count == 0)
{
    Console.Error.WriteLine("No drives to scan.");
    return 1;
}

var reporter = new ConsoleReporter(options);
reporter.PrintBanner(roots);

// This writer sets up the schema once and is then reused for the first drive; the pipeline creates
// (and disposes) one additional writer per remaining drive so drives can scan in parallel.
using var writer = new DatabaseWriter(options);

try
{
    await writer.InitializeAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Failed to connect / initialize database:");
    Console.Error.WriteLine("  " + ex.Message);
    return 3;
}

var sw = Stopwatch.StartNew();
(int exitCode, ScanTotals totals) = await new ScanPipeline(options, reporter).RunAsync(roots, writer, cts.Token);
sw.Stop();

reporter.PrintSummary(totals, sw.Elapsed);

// Once the scan completes cleanly, surface duplicates straight away (unless suppressed, or there
// are no hashes to compare). Use --no-analyze to skip, or --analyze on its own to re-run later.
if (exitCode == 0 && options.ComputeHash && !options.NoAnalyze)
{
    Console.WriteLine();
    try
    {
        var analyzer = new DuplicateAnalyzer(options);
        DuplicateAnalysis analysis =
            await analyzer.FindTopDuplicatesAsync(options.TopN, samplePathsPerGroup: 5, cts.Token);
        reporter.PrintDuplicates(analysis, options.TopN);
    }
    catch (OperationCanceledException) { /* user canceled; summary already printed */ }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Scan succeeded, but duplicate analysis failed:");
        Console.Error.WriteLine("  " + ex.Message);
    }
}

return exitCode;
