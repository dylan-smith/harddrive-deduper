using System.Collections.ObjectModel;

namespace DupeHunter;

/// <summary>
/// One duplicate set in a YAML report: identical content found at two or more locations, with every
/// location listed. Unlike <see cref="DuplicateGroup"/> (a database query result), the locations are
/// mutable: a reviewing tool removes them as copies are deleted from disk, and the counts derive from
/// what remains.
/// </summary>
public sealed class DuplicateReportSet
{
    public required string ContentHash { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>The name every copy shares, or null when <see cref="NamesDiffer"/>.</summary>
    public string? Name { get; init; }

    /// <summary>True when the copies don't all use one file/folder name.</summary>
    public bool NamesDiffer { get; init; }

    /// <summary>Every location this content lives at (all copies, not a sample).</summary>
    public Collection<string> Locations { get; } = [];

    public int CopyCount => Locations.Count;

    /// <summary>Reclaimable space: the redundant copies (count − 1) × size.</summary>
    public long WastedBytes => Math.Max(0, Locations.Count - 1) * SizeBytes;
}

/// <summary>
/// The in-memory form of a YAML duplicate report: the metadata stamped by the CLI analysis plus the
/// duplicate file and folder sets with all of their locations. This is the GUI's working model — it
/// reviews and edits the report directly (see <see cref="RemoveFileCopy"/> /
/// <see cref="RemoveFolderCopy"/>) without ever opening the scan database, and persists the result
/// back through <see cref="DuplicateYamlWriter"/>.
/// </summary>
public sealed class DuplicateReport
{
    public DateTime GeneratedUtc { get; set; }

    /// <summary>The wasted-space floor the sets were filtered by when the report was generated.</summary>
    public long ThresholdBytes { get; set; }

    /// <summary>
    /// Reclaimable space across every duplicate <em>file</em> set in the analyzed scans — including
    /// sets below <see cref="ThresholdBytes"/>, which this report doesn't list. Removals here can only
    /// subtract what the report tracks, so after edits the total may slightly overstate what's left
    /// (by the below-threshold waste inside deleted folder trees).
    /// </summary>
    public long TotalWastedBytes { get; set; }

    public Collection<ScanRef> Scans { get; } = [];

    public Collection<DuplicateReportSet> FileSets { get; } = [];

    public Collection<DuplicateReportSet> FolderSets { get; } = [];

    /// <summary>Build a report from a completed analysis (one whose groups carry all their locations).</summary>
    public static DuplicateReport FromAnalysis(DuplicateAnalysis analysis, long thresholdBytes, DateTime generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var report = new DuplicateReport
        {
            GeneratedUtc = generatedUtc,
            ThresholdBytes = thresholdBytes,
            TotalWastedBytes = analysis.TotalWastedBytes,
        };
        foreach (var scan in analysis.Scans)
        {
            report.Scans.Add(scan);
        }
        foreach (var group in analysis.Groups)
        {
            report.FileSets.Add(ToSet(group));
        }
        foreach (var group in analysis.FolderGroups)
        {
            report.FolderSets.Add(ToSet(group));
        }
        return report;
    }

    private static DuplicateReportSet ToSet(DuplicateGroup group)
    {
        var namesDiffer = group.DistinctNameCount > 1;
        var set = new DuplicateReportSet
        {
            ContentHash = group.ContentHash,
            SizeBytes = group.SizeBytes,
            Name = namesDiffer ? null : group.FileName,
            NamesDiffer = namesDiffer,
        };
        foreach (var path in group.SamplePaths)
        {
            set.Locations.Add(path);
        }
        return set;
    }

    /// <summary>
    /// Record the deletion of one copy of a duplicate <em>file</em> set. The location is removed and
    /// a set left with fewer than two copies is no longer a duplicate, so it drops from the report.
    /// Returns false when the location wasn't in the set (already removed). Idempotent.
    /// </summary>
    public bool RemoveFileCopy(DuplicateReportSet set, string path)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return RemoveCopy(set, path, FileSets, countsTowardTotal: true);
    }

    /// <summary>
    /// Record the deletion of one copy of a duplicate <em>folder</em> set. Deleting the tree also
    /// deleted everything inside it, so besides the folder's own entry, every location of every other
    /// set (file or folder) that lived under the tree is stripped too — the same cascade the database
    /// bookkeeping used to do. Sets left with fewer than two copies drop from the report.
    /// </summary>
    public void RemoveFolderCopy(DuplicateReportSet set, string folderPath)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrEmpty(folderPath);

        RemoveCopy(set, folderPath, FolderSets, countsTowardTotal: false);
        RemoveCopiesUnder(folderPath, FileSets, countsTowardTotal: true);
        RemoveCopiesUnder(folderPath, FolderSets, countsTowardTotal: false);
    }

    /// <summary>
    /// Remove one location from a set, keeping <see cref="TotalWastedBytes"/> in step when asked (the
    /// total counts file sets only — folder sets overlap the same bytes). Removing a copy from a set
    /// that still had redundancy reclaims exactly one file size.
    /// </summary>
    private bool RemoveCopy(DuplicateReportSet set, string path, Collection<DuplicateReportSet> sets, bool countsTowardTotal)
    {
        var index = IndexOf(set.Locations, path);
        if (index < 0)
        {
            return false;
        }

        if (countsTowardTotal && set.Locations.Count >= 2)
        {
            TotalWastedBytes = Math.Max(0, TotalWastedBytes - set.SizeBytes);
        }
        set.Locations.RemoveAt(index);

        if (set.Locations.Count < 2)
        {
            sets.Remove(set);
        }
        return true;
    }

    /// <summary>Strip every location lying strictly under <paramref name="folderPath"/> from every set.</summary>
    private void RemoveCopiesUnder(string folderPath, Collection<DuplicateReportSet> sets, bool countsTowardTotal)
    {
        var prefix = folderPath.TrimEnd('\\') + '\\';
        for (var i = sets.Count - 1; i >= 0; i--)
        {
            var set = sets[i];
            for (var j = set.Locations.Count - 1; j >= 0; j--)
            {
                if (set.Locations[j].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (countsTowardTotal && set.Locations.Count >= 2)
                    {
                        TotalWastedBytes = Math.Max(0, TotalWastedBytes - set.SizeBytes);
                    }
                    set.Locations.RemoveAt(j);
                }
            }

            if (set.Locations.Count < 2)
            {
                sets.RemoveAt(i);
            }
        }
    }

    /// <summary>Case-insensitive location lookup (Windows path semantics).</summary>
    private static int IndexOf(Collection<string> locations, string path)
    {
        for (var i = 0; i < locations.Count; i++)
        {
            if (string.Equals(locations[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }
}
