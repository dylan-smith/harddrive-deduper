namespace DupeHunter;

/// <summary>Decides which drive roots a scan should walk.</summary>
internal static class DriveResolver
{
    /// <summary>
    /// The drives named on the command line, or — when none were given — every ready fixed drive.
    /// </summary>
    public static IReadOnlyList<string> ResolveRoots(Options options)
    {
        return options.Drives.Count > 0
            ? options.Drives
            : DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
    }
}
