using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DupeHunter.Gui.Services;

namespace DupeHunter.Gui.ViewModels;

/// <summary>
/// One duplicate set (file or folder) in the list: its summary columns, its lazily loaded member
/// locations, and the delete orchestration those members trigger. When deletions bring the set
/// down to a single remaining copy it is no longer a duplicate and removes itself from the list.
/// </summary>
public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    private readonly DuplicateGroup _group;
    private readonly DbSession _session;
    private readonly IDialogService _dialogs;
    private readonly Action<DuplicateGroupViewModel> _onResolved;
    private readonly Func<Task> _onFolderDeleted;
    private bool _membersLoaded;
    private int _goneCount;

    public DuplicateGroupViewModel(
        DuplicateGroup group, bool isFolder, DbSession session, IDialogService dialogs,
        Action<DuplicateGroupViewModel> onResolved, Func<Task> onFolderDeleted)
    {
        _group = group;
        IsFolder = isFolder;
        _session = session;
        _dialogs = dialogs;
        _onResolved = onResolved;
        _onFolderDeleted = onFolderDeleted;
    }

    public bool IsFolder { get; }

    public string Name => _group.FileName;

    /// <summary>The copies don't all share one name; <see cref="Name"/> is just one of them.</summary>
    public bool NamesDiffer => _group.DistinctNameCount > 1;

    public string DisplayName => NamesDiffer ? Name + " (names differ)" : Name;

    public long SizeBytes => _group.SizeBytes;

    public int OriginalCopyCount => _group.CopyCount;

    /// <summary>Copies still on disk, as deletions this session are subtracted.</summary>
    public int RemainingCopies => _group.CopyCount - _goneCount;

    /// <summary>Space still reclaimable from this set: the redundant remaining copies × size.</summary>
    public long WastedBytes => Math.Max(0, RemainingCopies - 1) * _group.SizeBytes;

    public ObservableCollection<GroupMemberViewModel> Members { get; } = [];

    [ObservableProperty]
    private bool isLoadingMembers;

    /// <summary>Load all member locations the first time the group is selected.</summary>
    public async Task EnsureMembersLoadedAsync()
    {
        if (_membersLoaded || IsLoadingMembers)
        {
            return;
        }

        IsLoadingMembers = true;
        try
        {
            var paths = await Task.Run(() => IsFolder
                ? _session.Analyzer.GetFolderGroupLocationsAsync(_group.ContentHash, _group.SizeBytes, CancellationToken.None)
                : _session.Analyzer.GetFileGroupLocationsAsync(_group.ContentHash, _group.SizeBytes, CancellationToken.None));

            Members.Clear();
            foreach (var path in paths)
            {
                Members.Add(new GroupMemberViewModel(this, path));
            }

            _membersLoaded = true;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
        {
            _dialogs.ShowError("Couldn't load locations", ex.Message);
        }
        finally
        {
            IsLoadingMembers = false;
        }
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
                $"This entry no longer exists on disk:\n\n{member.FullPath}\n\nRemove its stale record from the database?"))
            {
                await _session.DeleteService.RemoveStaleRowsAsync(member.FullPath, IsFolder, CancellationToken.None);
                MarkGone(member, MemberState.Removed);
                await NotifyIfFolderContentChangedAsync();
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
            await NotifyIfFolderContentChangedAsync();
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

        await NotifyIfFolderContentChangedAsync();

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

    /// <summary>Delete one copy from disk and database, updating its status. No confirmation here.</summary>
    private async Task<bool> DeleteCoreAsync(GroupMemberViewModel member)
    {
        var outcome = await _session.DeleteService.DeleteAsync(member.FullPath, IsFolder, CancellationToken.None);
        switch (outcome.Status)
        {
            case DeleteStatus.Deleted:
                MarkGone(member, MemberState.Deleted);
                return true;

            case DeleteStatus.AlreadyMissing:
                // Vanished between listing and deleting; the database row is stale either way.
                await _session.DeleteService.RemoveStaleRowsAsync(member.FullPath, IsFolder, CancellationToken.None);
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

    private void MarkGone(GroupMemberViewModel member, MemberState state)
    {
        member.ErrorMessage = null;
        member.State = state;
        _goneCount++;
        OnPropertyChanged(nameof(RemainingCopies));
        OnPropertyChanged(nameof(WastedBytes));

        if (RemainingCopies < 2)
        {
            _onResolved(this);
        }
    }

    /// <summary>
    /// A deleted folder tree also removed file rows that participate in file groups, so the file
    /// view must re-query after any folder deletion.
    /// </summary>
    private Task NotifyIfFolderContentChangedAsync() => IsFolder ? _onFolderDeleted() : Task.CompletedTask;
}
