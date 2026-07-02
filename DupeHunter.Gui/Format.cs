namespace DupeHunter.Gui;

/// <summary>Display formatting shared by views and dialogs.</summary>
public static class Format
{
    /// <summary>A byte count in human units, e.g. "1.4 GB".</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
