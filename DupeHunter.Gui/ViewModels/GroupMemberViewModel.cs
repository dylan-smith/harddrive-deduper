using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DupeHunter.Gui.Services;

namespace DupeHunter.Gui.ViewModels;

public enum MemberState
{
    /// <summary>On disk (as far as we know) and still listed in the database.</summary>
    Present,

    /// <summary>Permanently deleted from disk this session.</summary>
    Deleted,

    /// <summary>Was already missing from disk; its stale database rows were removed.</summary>
    Removed,

    /// <summary>The last delete attempt failed; see <see cref="ErrorMessage"/>.</summary>
    Failed,
}

/// <summary>One location (copy) of a duplicate set, with its per-copy actions.</summary>
public sealed partial class GroupMemberViewModel : ObservableObject
{
    private readonly DuplicateGroupViewModel _group;

    public GroupMemberViewModel(DuplicateGroupViewModel group, string fullPath)
    {
        _group = group;
        FullPath = fullPath;
    }

    public string FullPath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGone), nameof(StatusText), nameof(IsError))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(KeepThisCommand))]
    private MemberState state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsError))]
    private string? errorMessage;

    /// <summary>True when this copy no longer exists on disk (deleted here, or found missing).</summary>
    public bool IsGone => State is MemberState.Deleted or MemberState.Removed;

    public bool IsError => State == MemberState.Failed;

    public string StatusText => State switch
    {
        MemberState.Deleted => "deleted",
        MemberState.Removed => "was missing — removed from DB",
        MemberState.Failed => ErrorMessage ?? "failed",
        _ => "",
    };

    private bool CanAct() => !IsGone;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task DeleteAsync() => _group.DeleteMemberAsync(this);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task KeepThisAsync() => _group.KeepOnlyAsync(this);

    [RelayCommand]
    private void OpenInExplorer() => ExplorerService.Reveal(FullPath);
}
