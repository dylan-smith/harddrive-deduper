using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DupeHunter.Gui.Services;

namespace DupeHunter.Gui.ViewModels;

/// <summary>
/// The main window: which YAML duplicate report is open, the duplicate file/folder group lists built
/// from it, and the refresh/search/filter plumbing around them. All state comes from the report file
/// the CLI wrote — the scan database is never opened.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settings;
    private ReportSession? _session;

    public MainViewModel(IDialogService dialogs, SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _settings = settings;

        var saved = settings.Load();
        reportPath = saved.LastReportPath ?? "";
        minWastedMb = saved.MinWastedMb;

        FileGroupsView = CollectionViewSource.GetDefaultView(FileGroups);
        FileGroupsView.Filter = MatchesSearch;
        FolderGroupsView = CollectionViewSource.GetDefaultView(FolderGroups);
        FolderGroupsView.Filter = MatchesSearch;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string reportPath;

    [ObservableProperty]
    private double minWastedMb;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool isBusy;

    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    private string statusText = "Open a dupehunter duplicates report (.yml) to begin.";

    [ObservableProperty]
    private string scanSummary = "";

    [ObservableProperty]
    private string totalWastedText = "";

    public ObservableCollection<DuplicateGroupViewModel> FileGroups { get; } = [];

    public ObservableCollection<DuplicateGroupViewModel> FolderGroups { get; } = [];

    public ICollectionView FileGroupsView { get; }

    public ICollectionView FolderGroupsView { get; }

    partial void OnSearchTextChanged(string value)
    {
        FileGroupsView.Refresh();
        FolderGroupsView.Refresh();
    }

    private bool MatchesSearch(object item) =>
        string.IsNullOrWhiteSpace(SearchText)
        || (item is DuplicateGroupViewModel g && g.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private async Task OpenReportAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open duplicate report",
            Filter = "Duplicate report (*.yml;*.yaml)|*.yml;*.yaml|All files (*.*)|*.*",
            FileName = ReportPath,
        };
        if (dialog.ShowDialog() == true)
        {
            ReportPath = dialog.FileName;
            await RefreshAsync();
        }
    }

    private bool CanRefresh() => !IsBusy && !string.IsNullOrWhiteSpace(ReportPath);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!File.Exists(ReportPath))
        {
            _dialogs.ShowError("Report not found",
                $"No file at:\n{ReportPath}\n\nRun the dupehunter CLI first — it writes a duplicates-<timestamp>.yml report — then open that file.");
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Loading report…";
            var session = await Task.Run(() => ReportSession.LoadAsync(ReportPath, CancellationToken.None));

            _session = session;
            Populate(FileGroups, session.Report.FileSets, isFolder: false);
            Populate(FolderGroups, session.Report.FolderSets, isFolder: true);

            ScanSummary = session.Report.Scans.Count == 0
                ? "No scans recorded in this report."
                : "Scans: " + string.Join("   ", session.Report.Scans.Select(s => $"{s.Drive} {s.CompletedAtUtc.ToLocalTime():g}"));
            TotalWastedText = $"Total reclaimable: {Format.Bytes(session.Report.TotalWastedBytes)}";
            StatusText = $"{FileGroups.Count} duplicate file sets, {FolderGroups.Count} duplicate folder sets over {MinWastedMb:0.#} MB wasted.";

            _settings.Save(new AppSettings { LastReportPath = ReportPath, MinWastedMb = MinWastedMb });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Couldn't load the report", ex.Message);
            StatusText = "Load failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fill a list from the report's sets, hiding those below the min-wasted view filter (the report
    /// itself keeps them — the filter only affects what's shown).
    /// </summary>
    private void Populate(ObservableCollection<DuplicateGroupViewModel> target, IEnumerable<DuplicateReportSet> sets, bool isFolder)
    {
        var minBytes = (long)(MinWastedMb * 1024 * 1024);
        target.Clear();
        foreach (var set in sets.Where(s => s.WastedBytes >= minBytes))
        {
            target.Add(new DuplicateGroupViewModel(set, isFolder, _session!, _dialogs, RemoveResolvedGroup, RefreshFileGroupsAsync));
        }
    }

    /// <summary>A set is down to one copy — it's no longer a duplicate, so drop it from its list.</summary>
    private void RemoveResolvedGroup(DuplicateGroupViewModel group)
    {
        FileGroups.Remove(group);
        FolderGroups.Remove(group);
        StatusText = $"{FileGroups.Count} duplicate file sets, {FolderGroups.Count} duplicate folder sets.";
    }

    /// <summary>
    /// Rebuild just the file groups after a folder tree was deleted (its files were group members
    /// too, and the report cascade stripped them). Cheap — it re-reads the in-memory report, and
    /// folder-set state is kept.
    /// </summary>
    private Task RefreshFileGroupsAsync()
    {
        if (_session is null)
        {
            return Task.CompletedTask;
        }

        Populate(FileGroups, _session.Report.FileSets, isFolder: false);
        TotalWastedText = $"Total reclaimable: {Format.Bytes(_session.Report.TotalWastedBytes)}";
        StatusText = $"{FileGroups.Count} duplicate file sets, {FolderGroups.Count} duplicate folder sets (file view refreshed after folder delete).";
        return Task.CompletedTask;
    }
}
