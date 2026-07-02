using DupeHunter.Gui.Services;

namespace DupeHunter.Gui;

/// <summary>
/// Everything bound to one open database file: the CLI-shared query/mutation objects and the
/// delete service built on them. A new session replaces the old one when another database is
/// opened or the view is refreshed.
/// </summary>
public sealed class DbSession
{
    public DbSession(string databasePath)
    {
        Options = new Options { DatabasePath = databasePath };
        Analyzer = new DuplicateAnalyzer(Options);
        Mutator = new DuplicateMutator(Options);
        DeleteService = new DeleteService(Mutator);
    }

    public Options Options { get; }
    public DuplicateAnalyzer Analyzer { get; }
    public DuplicateMutator Mutator { get; }
    public IDeleteService DeleteService { get; }
}
