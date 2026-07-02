using System.Diagnostics;
using DupeHunter;

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

// Analysis mode reads an already-scanned table and exits; it never touches the drives. A successful
// analysis is followed by an automatic database cleanup unless --no-cleanup is given.
if (options.Analyze)
{
    var analysisReporter = new ConsoleReporter(options);
    try
    {
        var analyzer = new DuplicateAnalyzer(options);
        DuplicateAnalysis analysis;
        await using (var status = new StepProgress("Analyzing duplicates…"))
        {
            analysis = await analyzer.FindTopDuplicatesAsync(options.TopN, samplePathsPerGroup: 5, cts.Token, status);
        }
        analysisReporter.PrintDuplicates(analysis, options.TopN);
        await WriteYamlReportAsync(analyzer, cts.Token);
        await MaybeCleanupAfterAnalysisAsync(cts.Token);
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
        await RunCleanupAsync(cts.Token);
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

var roots = DriveResolver.ResolveRoots(options);
if (roots.Count == 0)
{
    Console.Error.WriteLine("No drives to scan.");
    return 1;
}

var reporter = new ConsoleReporter(options);
reporter.PrintBanner(roots);

// All drives share one SQLite file, so writes serialize through this single lock while scanning and
// hashing stay parallel. The primary writer (below) and every per-drive writer the pipeline creates
// are constructed with this same instance.
var writeLock = new SemaphoreSlim(1, 1);

// This writer sets up the schema once and is then reused for the first drive; the pipeline creates
// (and disposes) one additional writer per remaining drive so drives can scan in parallel.
using var writer = new DatabaseWriter(options, writeLock);

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
(var exitCode, var totals) = await new ScanPipeline(options, reporter, writeLock).RunAsync(roots, writer, cts.Token);
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
        DuplicateAnalysis analysis;
        await using (var status = new StepProgress("Analyzing duplicates…"))
        {
            analysis = await analyzer.FindTopDuplicatesAsync(options.TopN, samplePathsPerGroup: 5, cts.Token, status);
        }
        reporter.PrintDuplicates(analysis, options.TopN);
        await WriteYamlReportAsync(analyzer, cts.Token);
        await MaybeCleanupAfterAnalysisAsync(cts.Token);
    }
    catch (OperationCanceledException) { /* user canceled; summary already printed */ }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Scan succeeded, but duplicate analysis failed:");
        Console.Error.WriteLine("  " + ex.Message);
    }
}

return exitCode;

// Prune the database to the latest completed scan per drive, printing the plan and result. Shared by
// the standalone --cleanup mode and the automatic post-analysis cleanup. Honors --dry-run.
async Task RunCleanupAsync(CancellationToken ct)
{
    var cleaner = new DatabaseCleaner(options);
    Console.WriteLine("Determining retained runs (latest completed scan per drive)...");
    var plan = await cleaner.PlanAsync(ct);

    Console.WriteLine($"Retaining {plan.KeptScans.Count} run(s):");
    foreach (var s in plan.KeptScans)
    {
        Console.WriteLine($"  {s.Drive,-6} {s.ScanRunId}  completed {s.CompletedAtUtc:u}");
    }

    Console.WriteLine($"Rows to delete: {plan.FilesToDelete:n0} file/folder row(s), {plan.SkipsToDelete:n0} skip(s).");
    Console.WriteLine("The scan audit log is preserved.");

    if (plan.FilesToDelete == 0 && plan.SkipsToDelete == 0)
    {
        Console.WriteLine("Nothing to delete; database is already clean.");
        return;
    }

    if (options.DryRun)
    {
        Console.WriteLine("Dry run: no rows were deleted.");
        return;
    }

    var progress = new Progress<long>(n => Console.Write($"\rDeleting... {n:n0} rows removed"));
    var result = await cleaner.ExecuteAsync(progress, ct);
    Console.WriteLine();
    Console.WriteLine($"Deleted {result.FilesDeleted:n0} file row(s) and {result.SkipsDeleted:n0} skip row(s).");
}

// Write the YAML duplicate report (every file/folder set wasting at least the threshold, with all of
// their locations) unless --no-yaml was given. Re-queries uncapped rather than reusing the top-N
// console results, which are both limited and sampled. Non-fatal: a write failure warns and carries on.
async Task WriteYamlReportAsync(DuplicateAnalyzer analyzer, CancellationToken ct)
{
    if (!options.WriteYaml)
    {
        return;
    }

    try
    {
        // Stamp the filename and the report's generatedUtc with one timestamp so they agree. The default
        // name carries the timestamp so successive runs each write a fresh file instead of clobbering.
        var generatedUtc = DateTime.UtcNow;
        var outputPath = options.YamlOutputPath
            ?? $"duplicates-{generatedUtc:yyyyMMdd-HHmmss}.yml";

        DuplicateAnalysis report;
        await using (var status = new StepProgress("Building the full duplicate report…"))
        {
            report = await analyzer.FindDuplicatesOverThresholdAsync(options.YamlThresholdBytes, ct, status);
        }
        await DuplicateYamlWriter.WriteAsync(
            outputPath, DuplicateReport.FromAnalysis(report, options.YamlThresholdBytes, generatedUtc), ct);

        var thresholdMb = options.YamlThresholdBytes / (1024.0 * 1024.0);
        Console.WriteLine();
        Console.WriteLine(
            $"Wrote {report.Groups.Count} file set(s) and {report.FolderGroups.Count} folder set(s) " +
            $"wasting ≥ {thresholdMb:0.##} MB to {Path.GetFullPath(outputPath)}");
    }
    catch (OperationCanceledException) { /* user canceled; nothing partial to report */ }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Analysis succeeded, but writing the YAML report failed:");
        Console.Error.WriteLine("  " + ex.Message);
    }
}

// Run cleanup after a successful analysis unless suppressed. A cleanup failure here is non-fatal: the
// analysis already succeeded, so warn and carry on rather than reporting overall failure.
async Task MaybeCleanupAfterAnalysisAsync(CancellationToken ct)
{
    if (options.NoCleanup)
    {
        return;
    }

    Console.WriteLine();
    try
    {
        await RunCleanupAsync(ct);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\nCleanup canceled.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Analysis succeeded, but cleanup failed:");
        Console.Error.WriteLine("  " + ex.Message);
    }
}
