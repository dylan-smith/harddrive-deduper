using System.Text;

namespace DupeHunter;

public sealed partial class Options
{
    public static string HelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("dupehunter - scan drives and record every file (with a content hash) into a SQLite database file.");
        sb.AppendLine();
        sb.AppendLine("USAGE:");
        sb.AppendLine("  dupehunter [options]");
        sb.AppendLine();
        sb.AppendLine("OPTIONS:");
        sb.AppendLine("  -d, --drives <list>          Comma-separated drives (e.g. C,D or C:\\,E:\\).");
        sb.AppendLine("                               Scan mode: drives to scan; each drive is its own scan run.");
        sb.AppendLine("                               Omit to scan ALL fixed drives.");
        sb.AppendLine("                               Analyze mode: drives whose latest scans to combine.");
        sb.AppendLine("  -c, --db, --database <path>  SQLite database file. Created if it doesn't exist.");
        sb.AppendLine("                               Default: dupehunter.db (in the current directory).");
        sb.AppendLine("  -t, --table <name>           Destination table. Default: Files");
        sb.AppendLine("      --skip-table <name>      Table for skipped (inaccessible) directories.");
        sb.AppendLine("                               Default: ScanSkips");
        sb.AppendLine("      --scan-table <name>      Audit log of scan runs (start/finish time, status).");
        sb.AppendLine("                               Default: Scans (never dropped by --recreate)");
        sb.AppendLine("      --no-hash                Record metadata only; skip content hashing (much faster).");
        sb.AppendLine("      --no-folder-hash         Skip per-folder content fingerprints (still hashes files).");
        sb.AppendLine("      --max-hash-mb <n>        Skip hashing files larger than n MB (still records metadata).");
        sb.AppendLine("      --batch-size <n>         Rows per insert transaction. Default: 5000");
        sb.AppendLine("      --parallelism <n>        Hashing threads. Default: processor count.");
        sb.AppendLine("      --recreate               Drop and recreate the table before scanning.");
        sb.AppendLine("      --follow-links           Follow directory symlinks/junctions (off by default).");
        sb.AppendLine("  -h, --help                   Show this help.");
        sb.AppendLine();
        sb.AppendLine("ANALYSIS (runs automatically after a scan; reports duplicate files):");
        sb.AppendLine("      --analyze, --duplicates  Analyze an existing table WITHOUT scanning. Combines the");
        sb.AppendLine("                               latest completed scan of each drive (or just the drives");
        sb.AppendLine("                               named with --drives) and reports files whose content");
        sb.AppendLine("                               (hash + size) appears in multiple locations, ranked by");
        sb.AppendLine("                               wasted space.");
        sb.AppendLine("      --no-analyze             Scan only; skip the post-scan duplicate analysis.");
        sb.AppendLine("      --top <n>                Number of duplicate sets to list. Default: 10");
        sb.AppendLine();
        sb.AppendLine("YAML REPORT (written automatically as part of analysis):");
        sb.AppendLine("                               Lists EVERY duplicate file and folder set wasting at least");
        sb.AppendLine("                               the threshold (not just the top N), with all locations.");
        sb.AppendLine("      --yaml-out <path>        Output file. Default: duplicates-<UTC timestamp>.yml");
        sb.AppendLine("      --yaml-threshold-mb <n>  Min wasted space (MB) for a set to be included. Default: 100");
        sb.AppendLine("      --no-yaml                Skip writing the YAML report.");
        sb.AppendLine();
        sb.AppendLine("CLEANUP (prune the database; does NOT scan):");
        sb.AppendLine("      --cleanup, --clean       Keep only the latest COMPLETED scan of each drive and");
        sb.AppendLine("                               delete every other run's file/skip rows. Considers all");
        sb.AppendLine("                               drives regardless of --drives. The scan audit log");
        sb.AppendLine("                               (Scans table) is preserved. Assumes no scan is running.");
        sb.AppendLine("                               Runs automatically after a successful analysis.");
        sb.AppendLine("      --no-cleanup             Skip the cleanup that otherwise runs after analysis.");
        sb.AppendLine("      --dry-run                With cleanup, report what would be deleted, delete nothing.");
        sb.AppendLine();
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine("  dupehunter --drives C,D");
        sb.AppendLine("  dupehunter --db D:\\index\\dupehunter.db --drives C");
        sb.AppendLine("  dupehunter --drives C --no-hash --recreate");
        sb.AppendLine("  dupehunter --analyze");
        sb.AppendLine("  dupehunter --analyze --top 25");
        sb.AppendLine("  dupehunter --analyze --drives C,D   (combine only C and D's latest scans)");
        sb.AppendLine("  dupehunter --cleanup --dry-run      (preview what cleanup would delete)");
        sb.AppendLine("  dupehunter --cleanup                (prune to the latest completed scan per drive)");
        return sb.ToString();
    }
}
