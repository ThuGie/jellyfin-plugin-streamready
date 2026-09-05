using Jellyfin.Plugin.StreamReady.Configuration;

namespace Jellyfin.Plugin.StreamReady.Services;

public static class EncodeWindow
{
    public static bool IsOpen(PluginConfiguration config, DateTime? localNow = null)
    {
        if (!config.EncodeWindowEnabled)
        {
            return true;
        }

        var now = localNow ?? DateTime.Now;
        if (!DayAllowed(config.EncodeWindowDays, now.DayOfWeek))
        {
            return false;
        }

        if (!TryParseHm(config.EncodeWindowStart, out var start)
            || !TryParseHm(config.EncodeWindowEnd, out var end))
        {
            return true;
        }

        var t = now.TimeOfDay;
        if (start == end)
        {
            return true;
        }

        if (start < end)
        {
            return t >= start && t < end;
        }

        // Overnight: e.g. 22:00–06:00
        return t >= start || t < end;
    }

    public static string Describe(PluginConfiguration config)
    {
        if (!config.EncodeWindowEnabled)
        {
            return "Any time";
        }

        var days = string.IsNullOrWhiteSpace(config.EncodeWindowDays)
            ? "every day"
            : "selected days";
        return $"{config.EncodeWindowStart}–{config.EncodeWindowEnd} ({days})";
    }

    private static bool DayAllowed(string? csv, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return true;
        }

        var wanted = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n >= 0 && n <= 6)
            .ToHashSet();
        return wanted.Count == 0 || wanted.Contains((int)day);
    }

    private static bool TryParseHm(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(':');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var h)
            || !int.TryParse(parts[1], out var m)
            || h is < 0 or > 23
            || m is < 0 or > 59)
        {
            return false;
        }

        time = new TimeSpan(h, m, 0);
        return true;
    }
}
