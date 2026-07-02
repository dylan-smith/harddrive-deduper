using System.Diagnostics;
using System.IO;

namespace DupeHunter.Gui.Services;

/// <summary>Opens Windows Explorer at a given file or folder.</summary>
public static class ExplorerService
{
    /// <summary>
    /// Show the path in Explorer, selected within its parent folder. Falls back to opening the
    /// parent folder itself when the entry no longer exists.
    /// </summary>
    public static void Reveal(string fullPath)
    {
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
            return;
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (parent is not null && Directory.Exists(parent))
        {
            Process.Start("explorer.exe", $"\"{parent}\"");
        }
    }
}
