namespace HarddriveDeduper;

/// <summary>Parsed command-line options. Help text lives in <c>Options.HelpText.cs</c>.</summary>
public sealed partial class Options
{
    /// <summary>Drive roots to scan, e.g. "C:\", "D:\". Empty means "all fixed drives".</summary>
    public List<string> Drives { get; } = new();

    public string ConnectionString { get; set; } =
        "Server=localhost;Database=FileInventory;Integrated Security=true;TrustServerCertificate=true;";

    public string TableName { get; set; } = "dbo.Files";

    /// <summary>Table recording directories that couldn't be enumerated during a scan.</summary>
    public string SkipTableName { get; set; } = "dbo.ScanSkips";

    /// <summary>
    /// Table recording one row per scan run (start/finish time and status), so that scans which
    /// never completed can be detected and excluded from analysis. This is a persistent audit log
    /// and is never dropped by <c>--recreate</c>.
    /// </summary>
    public string ScanTableName { get; set; } = "dbo.Scans";

    /// <summary>When true, file contents are hashed (SHA-256). Disable for a fast metadata-only inventory.</summary>
    public bool ComputeHash { get; set; } = true;

    /// <summary>Skip hashing files larger than this (bytes). 0 = no limit.</summary>
    public long MaxHashBytes { get; set; } = 0;

    /// <summary>Rows accumulated before each bulk-copy flush.</summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>Degree of parallelism for hashing. Defaults to processor count.</summary>
    public int Parallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>Drop &amp; recreate the destination table before scanning.</summary>
    public bool Recreate { get; set; }

    /// <summary>Follow directory reparse points (symlinks / junctions). Off by default to avoid loops.</summary>
    public bool FollowReparsePoints { get; set; }

    /// <summary>Run duplicate analysis against an already-scanned table instead of scanning drives.</summary>
    public bool Analyze { get; set; }

    /// <summary>Skip the duplicate analysis that otherwise runs automatically after a scan.</summary>
    public bool NoAnalyze { get; set; }

    /// <summary>How many duplicate sets to list in analysis mode (ranked by wasted space).</summary>
    public int TopN { get; set; } = 10;

    /// <summary>
    /// Prune the database instead of scanning: keep only the most recent completed scan of each drive
    /// and delete every other scan's file/skip rows. Assumes no scan is currently running.
    /// </summary>
    public bool Cleanup { get; set; }

    /// <summary>With <c>--cleanup</c>, report what would be deleted without deleting anything.</summary>
    public bool DryRun { get; set; }

    public bool ShowHelp { get; set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next(string name)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Option '{name}' requires a value.");
                return args[++i];
            }

            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "-?":
                    o.ShowHelp = true;
                    break;

                case "-d":
                case "--drive":
                case "--drives":
                    foreach (var d in Next(arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        o.Drives.Add(NormalizeDrive(d));
                    break;

                case "-c":
                case "--connection-string":
                    o.ConnectionString = Next(arg);
                    break;

                case "-t":
                case "--table":
                    o.TableName = Next(arg);
                    break;

                case "--skip-table":
                    o.SkipTableName = Next(arg);
                    break;

                case "--scan-table":
                    o.ScanTableName = Next(arg);
                    break;

                case "--no-hash":
                    o.ComputeHash = false;
                    break;

                case "--max-hash-mb":
                    o.MaxHashBytes = long.Parse(Next(arg)) * 1024L * 1024L;
                    break;

                case "--batch-size":
                    o.BatchSize = int.Parse(Next(arg));
                    break;

                case "--parallelism":
                    o.Parallelism = Math.Max(1, int.Parse(Next(arg)));
                    break;

                case "--recreate":
                    o.Recreate = true;
                    break;

                case "--follow-links":
                    o.FollowReparsePoints = true;
                    break;

                case "--analyze":
                case "--duplicates":
                    o.Analyze = true;
                    break;

                case "--no-analyze":
                    o.NoAnalyze = true;
                    break;

                case "--top":
                    o.TopN = Math.Max(1, int.Parse(Next(arg)));
                    break;

                case "--cleanup":
                case "--clean":
                    o.Cleanup = true;
                    break;

                case "--dry-run":
                    o.DryRun = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown option: '{arg}'. Use --help for usage.");
            }
        }

        return o;
    }

    /// <summary>Turn "c" / "C:" / "C:\" into the canonical root form "C:\".</summary>
    private static string NormalizeDrive(string raw)
    {
        string s = raw.Trim().TrimEnd('\\', '/');
        if (s.Length == 1 && char.IsLetter(s[0]))
            s += ":";
        if (s.Length == 2 && char.IsLetter(s[0]) && s[1] == ':')
            return s.ToUpperInvariant() + "\\";
        // Fall back to whatever the user gave (could be a UNC path or mount point).
        return raw.EndsWith('\\') ? raw : raw + "\\";
    }
}
