using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DupeHunter.Gui.Services;

namespace DupeHunter.Gui.ViewModels;

/// <summary>
/// The main window: which database is open, the duplicate file/folder group lists, and the
/// refresh/search/filter plumbing around them.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settings;
    private DbSession? _session;

    public MainViewModel(IDialogService dialogs, SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _settings = settings;

        var saved = settings.Load();
        databasePath = saved.LastDatabasePath ?? "";
        minWastedMb = saved.MinWastedMb;

        FileGroupsView = CollectionViewSource.GetDefaultView(FileGroups);
        FileGroupsView.Filter = MatchesSearch;
        FolderGroupsView = CollectionViewSource.GetDefaultView(FolderGroups);
        FolderGroupsView.Filter = MatchesSearch;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string databasePath;

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
    private string statusText = "Open a DupeHunter database to begin.";

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
    private async Task OpenDatabaseAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open DupeHunter database",
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            FileName = DatabasePath,
        };
        if (dialog.ShowDialog() == true)
        {
            DatabasePath = dialog.FileName;
            await RefreshAsync();
        }
    }

    private bool CanRefresh() => !IsBusy && !string.IsNullOrWhiteSpace(DatabasePath);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        // Guard with File.Exists: opening a bad path would silently create an empty database file.
        if (!File.Exists(DatabasePath))
        {
            _dialogs.ShowError("Database not found", $"No file at:\n{DatabasePath}\n\nRun the dupehunter CLI scan first, or pick its .db file.");
            return;
        }

        IsBusy = true;
        try
        {
            var session = new DbSession(DatabasePath);
            var minBytes = (long)(MinWastedMb * 1024 * 1024);
            var progress = new Progress<string>(s => StatusText = s);
            var stepAdapter = new ProgressStepAdapter(progress);

            var analysis = await Task.Run(async () =>
            {
                await session.Mutator.EnsurePathIndexAsync(CancellationToken.None, stepAdapter);
                return await session.Analyzer.FindGroupSummariesAsync(minBytes, topN: null, CancellationToken.None, stepAdapter);
            });

            _session = session;
            Populate(FileGroups, analysis.Groups, isFolder: false);
            Populate(FolderGroups, analysis.FolderGroups, isFolder: true);

            ScanSummary = analysis.Scans.Count == 0
                ? "No completed scans in this database."
                : "Scans: " + string.Join("   ", analysis.Scans.Select(s => $"{s.Drive} {s.CompletedAtUtc.ToLocalTime():g}"));
            TotalWastedText = $"Total reclaimable: {Format.Bytes(analysis.TotalWastedBytes)}";
            StatusText = $"{FileGroups.Count} duplicate file sets, {FolderGroups.Count} duplicate folder sets over {MinWastedMb:0.#} MB wasted.";

            _settings.Save(new AppSettings { LastDatabasePath = DatabasePath, MinWastedMb = MinWastedMb });
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
        {
            _dialogs.ShowError("Analysis failed", ex.Message);
            StatusText = "Analysis failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Populate(ObservableCollection<DuplicateGroupViewModel> target, IReadOnlyList<DuplicateGroup> groups, bool isFolder)
    {
        target.Clear();
        foreach (var group in groups)
        {
            target.Add(new DuplicateGroupViewModel(group, isFolder, _session!, _dialogs, RemoveResolvedGroup, RefreshFileGroupsAsync));
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
    /// Re-query just the file groups after a folder tree was deleted (its files were group members
    /// too). Cheap — the folder containment analysis is not re-run, and folder-set state is kept.
    /// </summary>
    private async Task RefreshFileGroupsAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var minBytes = (long)(MinWastedMb * 1024 * 1024);
            var analysis = await Task.Run(() =>
                _session.Analyzer.FindFileGroupSummariesAsync(minBytes, topN: null, CancellationToken.None));

            Populate(FileGroups, analysis.Groups, isFolder: false);
            TotalWastedText = $"Total reclaimable: {Format.Bytes(analysis.TotalWastedBytes)}";
            StatusText = $"{FileGroups.Count} duplicate file sets, {FolderGroups.Count} duplicate folder sets (file view refreshed after folder delete).";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
        {
            StatusText = $"File view refresh failed: {ex.Message}";
        }
    }

    /// <summary>Routes the analyzer's step labels onto the UI thread as status-bar text.</summary>
    private sealed class ProgressStepAdapter(IProgress<string> progress) : IStepProgress
    {
        public void BeginStep(string label) => progress.Report(label);

        public void UpdateStep(string label) => progress.Report(label);
    }
}
