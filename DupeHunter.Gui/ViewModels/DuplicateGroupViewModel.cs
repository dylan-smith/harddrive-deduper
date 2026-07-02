using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DupeHunter.Gui.Services;

namespace DupeHunter.Gui.ViewModels;

/// <summary>
/// One duplicate set (file or folder) in the list: its summary columns, its member locations from
/// the report, and the delete orchestration those members trigger. Counts read live from the backing
/// report set, which shrinks as copies are deleted; when deletions bring the set down to a single
/// remaining copy it is no longer a duplicate and removes itself from the list (and the report).
/// </summary>
public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    private readonly DuplicateReportSet _set;
    private readonly ReportSession _session;
    private readonly IDialogService _dialogs;
    private readonly Action<DuplicateGroupViewModel> _onResolved;
    private readonly Func<Task> _onFolderDeleted;
    private bool _membersLoaded;

    public DuplicateGroupViewModel(
        DuplicateReportSet set, bool isFolder, ReportSession session, IDialogService dialogs,
        Action<DuplicateGroupViewModel> onResolved, Func<Task> onFolderDeleted)
    {
        ArgumentNullException.ThrowIfNull(set);
        _set = set;
        IsFolder = isFolder;
        _session = session;
        _dialogs = dialogs;
        _onResolved = onResolved;
        _onFolderDeleted = onFolderDeleted;
        OriginalCopyCount = set.CopyCount;
        Name = set.Name ?? (set.Locations.Count > 0 ? Path.GetFileName(set.Locations[0]) : "");
    }

    public bool IsFolder { get; }

    /// <summary>The shared name, or one member's name when the copies are named differently.</summary>
    public string Name { get; }

    /// <summary>The copies don't all share one name; <see cref="Name"/> is just one of them.</summary>
    public bool NamesDiffer => _set.NamesDiffer;

    public string DisplayName => NamesDiffer ? Name + " (names differ)" : Name;

    public long SizeBytes => _set.SizeBytes;

    /// <summary>The copy count when the report was opened, before this session's deletions.</summary>
    public int OriginalCopyCount { get; }

    /// <summary>Copies still on disk — live from the report set as deletions remove locations.</summary>
    public int RemainingCopies => _set.CopyCount;

    /// <summary>Space still reclaimable from this set: the redundant remaining copies × size.</summary>
    public long WastedBytes => _set.WastedBytes;

    public ObservableCollection<GroupMemberViewModel> Members { get; } = [];

    /// <summary>Materialize member rows from the report's locations the first time the group is opened.</summary>
    public void EnsureMembersLoaded()
    {
        if (_membersLoaded)
        {
            return;
        }

        foreach (var path in _set.Locations)
        {
            Members.Add(new GroupMemberViewModel(this, path));
        }
        _membersLoaded = true;
    }

    /// <summary>The per-member Delete button: guard, confirm, then delete one copy.</summary>
    public async Task DeleteMemberAsync(GroupMemberViewModel member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.IsGone)
        {
            return;
        }

        if (RemainingCopies <= 1)
        {
            _dialogs.ShowError("Last remaining copy",
                "This is the only remaining copy of this content — deleting it would lose the data, not deduplicate it.");
            return;
        }

        var exists = await Task.Run(() => IsFolder ? Directory.Exists(member.FullPath) : File.Exists(member.FullPath));
        if (!exists)
        {
            if (_dialogs.Confirm("Not found on disk",
                $"This entry no longer exists on disk:\n\n{member.FullPath}\n\nRemove its stale entry from the report?"))
            {
                RemoveFromReport(member.FullPath);
                MarkGone(member, MemberState.Removed);
                await FinishMutationAsync();
            }
            return;
        }

        var kind = IsFolder ? "folder and ALL of its contents" : "file";
        if (!_dialogs.ConfirmDanger("Delete permanently",
            $"Permanently delete this {kind}?\n\n{member.FullPath}\n\nSize: {Format.Bytes(SizeBytes)}\n\nThis does NOT use the Recycle Bin and cannot be undone."))
        {
            return;
        }

        if (await DeleteCoreAsync(member))
        {
            await FinishMutationAsync();
        }
    }

    /// <summary>
    /// The "Keep this, delete rest" button: one confirmation, then every other remaining copy is
    /// permanently deleted.
    /// </summary>
    public async Task KeepOnlyAsync(GroupMemberViewModel keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var doomed = Members.Where(m => m != keep && !m.IsGone).ToList();
        if (doomed.Count == 0)
        {
            _dialogs.ShowInfo("Nothing to delete", "Every other copy of this set is already gone.");
            return;
        }

        var reclaim = doomed.Count * SizeBytes;
        var message = new StringBuilder();
        message.AppendLine($"Keep:\n{keep.FullPath}");
        message.AppendLine();
        message.AppendLine($"Permanently delete these {doomed.Count} cop{(doomed.Count == 1 ? "y" : "ies")} ({Format.Bytes(reclaim)}):");
        message.AppendLine();
        const int maxListed = 15;
        foreach (var m in doomed.Take(maxListed))
        {
            message.AppendLine(m.FullPath);
        }
        if (doomed.Count > maxListed)
        {
            message.AppendLine($"… and {doomed.Count - maxListed} more");
        }
        message.AppendLine();
        message.Append("This does NOT use the Recycle Bin and cannot be undone.");

        if (!_dialogs.ConfirmDanger("Delete all other copies", message.ToString()))
        {
            return;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var member in doomed)
        {
            if (await DeleteCoreAsync(member))
            {
                deleted++;
            }
            else
            {
                failed++;
            }
        }

        if (deleted > 0)
        {
            await FinishMutationAsync();
        }

        if (failed > 0)
        {
            _dialogs.ShowError("Some deletions failed",
                $"Deleted {deleted} of {doomed.Count} copies; {failed} failed. Each failure's reason is shown next to its path.");
        }
        else
        {
            _dialogs.ShowInfo("Done", $"Deleted {deleted} cop{(deleted == 1 ? "y" : "ies")}, reclaiming {Format.Bytes(deleted * SizeBytes)}.");
        }
    }

    /// <summary>Delete one copy from disk and the report, updating its status. No confirmation here.</summary>
    private async Task<bool> DeleteCoreAsync(GroupMemberViewModel member)
    {
        var outcome = await _session.DeleteService.DeleteAsync(member.FullPath, IsFolder, CancellationToken.None);
        switch (outcome.Status)
        {
            case DeleteStatus.Deleted:
                RemoveFromReport(member.FullPath);
                MarkGone(member, MemberState.Deleted);
                return true;

            case DeleteStatus.AlreadyMissing:
                // Vanished between listing and deleting; the report entry is stale either way.
                RemoveFromReport(member.FullPath);
                MarkGone(member, MemberState.Removed);
                return true;

            case DeleteStatus.Locked:
                member.ErrorMessage = "in use by another process — close it and retry";
                member.State = MemberState.Failed;
                return false;

            case DeleteStatus.Failed:
            default:
                member.ErrorMessage = outcome.Error ?? "delete failed";
                member.State = MemberState.Failed;
                return false;
        }
    }

    /// <summary>
    /// Reflect one gone copy in the report. A deleted folder tree cascades: everything the report
    /// tracked underneath it (file copies and nested folder copies) is stripped as well.
    /// </summary>
    private void RemoveFromReport(string fullPath)
    {
        if (IsFolder)
        {
            _session.Report.RemoveFolderCopy(_set, fullPath);
        }
        else
        {
            _session.Report.RemoveFileCopy(_set, fullPath);
        }
    }

    private void MarkGone(GroupMemberViewModel member, MemberState state)
    {
        member.ErrorMessage = null;
        member.State = state;
        OnPropertyChanged(nameof(RemainingCopies));
        OnPropertyChanged(nameof(WastedBytes));

        if (RemainingCopies < 2)
        {
            _onResolved(this);
        }
    }

    /// <summary>
    /// After an action's deletions: persist the edited report, and when a folder tree was removed let
    /// the file view re-sync (the cascade stripped the tree's files from file sets too).
    /// </summary>
    private async Task FinishMutationAsync()
    {
        try
        {
            await _session.SaveAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Couldn't update the report file",
                $"The deletion succeeded, but rewriting the report failed:\n{ex.Message}\n\nThe change is kept in this window and the next successful save will include it.");
        }

        if (IsFolder)
        {
            await _onFolderDeleted();
        }
    }
}
