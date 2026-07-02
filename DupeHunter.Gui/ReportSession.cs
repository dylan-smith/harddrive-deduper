using System.IO;
using DupeHunter.Gui.Services;

namespace DupeHunter.Gui;

/// <summary>
/// Everything bound to one open YAML duplicate report: its path, the parsed report the views edit,
/// and the delete service. Deletions update the report in memory and <see cref="SaveAsync"/> rewrites
/// the file, so the report stays the single record of what's left to review — the scan database the
/// CLI produced it from is never opened, let alone modified. A new session replaces the old one when
/// another report is opened or the view is refreshed.
/// </summary>
public sealed class ReportSession
{
    private ReportSession(string reportPath, DuplicateReport report)
    {
        ReportPath = reportPath;
        Report = report;
    }

    public string ReportPath { get; }

    public DuplicateReport Report { get; }

    public IDeleteService DeleteService { get; } = new DeleteService();

    public static async Task<ReportSession> LoadAsync(string reportPath, CancellationToken ct) =>
        new(reportPath, await DuplicateYamlReader.LoadAsync(reportPath, ct));

    /// <summary>
    /// Rewrite the report file from the in-memory state. Written to a sibling temp file and swapped
    /// in, so a failure mid-write can never truncate the report.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        var tempPath = ReportPath + ".tmp";
        await DuplicateYamlWriter.WriteAsync(tempPath, Report, ct);
        File.Move(tempPath, ReportPath, overwrite: true);
    }
}
