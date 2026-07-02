using System.Globalization;
using System.Text;

namespace DupeHunter;

/// <summary>
/// Writes a <see cref="DuplicateReport"/> to a YAML file: every duplicate file and folder set whose
/// wasted space meets the configured threshold, with all of its locations listed so the report is
/// directly actionable (the GUI reviews and edits this file; a script could feed on it too). The YAML
/// is emitted by hand — there is no YAML dependency — so every string is double-quoted and escaped,
/// which lets Windows paths (backslashes) and awkward file names survive a round-trip through
/// <see cref="DuplicateYamlReader"/>.
/// </summary>
public static class DuplicateYamlWriter
{
    /// <summary>Serialize <paramref name="report"/> to <paramref name="path"/>.</summary>
    public static async Task WriteAsync(string path, DuplicateReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();

        sb.AppendLine("# Duplicate file/folder report produced by dupehunter.");
        sb.AppendLine($"# Every set below wastes at least {FormatBytes(report.ThresholdBytes)} ({report.ThresholdBytes} bytes) of disk space.");
        sb.AppendLine("# 'wastedBytes' is the space reclaimable by keeping one copy and deleting the rest.");
        sb.AppendLine();

        sb.AppendLine($"generatedUtc: {Q(Iso(report.GeneratedUtc))}");
        sb.AppendLine($"wastedSpaceThresholdBytes: {report.ThresholdBytes}");
        sb.AppendLine($"totalWastedBytes: {report.TotalWastedBytes}");

        WriteScans(sb, report.Scans);
        WriteSets(sb, "duplicateFileSets", report.FileSets);
        WriteSets(sb, "duplicateFolderSets", report.FolderSets);

        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private static void WriteScans(StringBuilder sb, IReadOnlyList<ScanRef> scans)
    {
        if (scans.Count == 0)
        {
            sb.AppendLine("scans: []");
            return;
        }

        sb.AppendLine("scans:");
        foreach (var s in scans)
        {
            sb.AppendLine($"  - drive: {Q(s.Drive)}");
            sb.AppendLine($"    scanRunId: {Q(s.ScanRunId)}");
            sb.AppendLine($"    completedUtc: {Q(Iso(s.CompletedAtUtc))}");
        }
    }

    private static void WriteSets(StringBuilder sb, string key, IReadOnlyList<DuplicateReportSet> sets)
    {
        if (sets.Count == 0)
        {
            sb.AppendLine($"{key}: []");
            return;
        }

        sb.AppendLine($"{key}:");
        foreach (var s in sets)
        {
            // A shared name is only meaningful when every copy uses it; otherwise emit null.
            sb.AppendLine($"  - name: {(s.Name is null ? "null" : Q(s.Name))}");
            sb.AppendLine($"    namesDiffer: {(s.NamesDiffer ? "true" : "false")}");
            sb.AppendLine($"    contentHash: {Q(s.ContentHash)}");
            sb.AppendLine($"    sizeBytes: {s.SizeBytes}");
            sb.AppendLine($"    copyCount: {s.CopyCount}");
            sb.AppendLine($"    wastedBytes: {s.WastedBytes}");

            sb.AppendLine("    locations:");
            foreach (var p in s.Locations)
            {
                sb.AppendLine($"      - {Q(p)}");
            }
        }
    }

    /// <summary>An ISO-8601 UTC timestamp (e.g. <c>2026-06-22T16:50:00Z</c>).</summary>
    internal static string Iso(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>Double-quote and escape a string for a YAML double-quoted scalar.</summary>
    private static string Q(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Render a byte count as a human-friendly size (e.g. "100 MB"), for the file's comments.</summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.##} {units[unit]}";
    }
}
