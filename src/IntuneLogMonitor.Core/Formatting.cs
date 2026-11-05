using System.Globalization;

namespace IntuneLogMonitor.Core;

public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Formatiert eine Bytezahl lesbar (Basis 1024), z. B. "1,20 GB".</summary>
    public static string FormatBytes(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(culture, $"{bytes} B")
            : string.Create(culture, $"{value:0.00} {Units[unit]}");
    }

    public static string FormatRate(double? bytesPerSecond, CultureInfo? culture = null) =>
        bytesPerSecond is { } r ? FormatBytes((long)Math.Round(r), culture) + "/s" : "";

    /// <summary>Formatiert eine Dauer als "h:mm:ss" bzw. "m:ss".</summary>
    public static string FormatDuration(TimeSpan? span)
    {
        if (span is not { } s) return "";
        if (s < TimeSpan.Zero) s = TimeSpan.Zero;
        return s.TotalHours >= 1
            ? $"{(int)s.TotalHours}:{s.Minutes:00}:{s.Seconds:00}"
            : $"{s.Minutes}:{s.Seconds:00}";
    }
}
