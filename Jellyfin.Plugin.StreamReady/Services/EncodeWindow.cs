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
        if (!TryParseHm(config.EncodeWindowStart, out var start)
            || !TryParseHm(config.EncodeWindowEnd, out var end))
        {
            return false;
        }

        var t = now.TimeOfDay;
        if (start == end)
        {
            return DayAllowed(config.EncodeWindowDays, now.DayOfWeek);
        }

        if (start < end)
        {
            return DayAllowed(config.EncodeWindowDays, now.DayOfWeek) && t >= start && t < end;
        }

        // Overnight: e.g. 22:00–06:00
        return (t >= start && DayAllowed(config.EncodeWindowDays, now.DayOfWeek))
            || (t < end && DayAllowed(config.EncodeWindowDays, now.AddDays(-1).DayOfWeek));
    }

    public static string Describe(PluginConfiguration config)
    {
        if (!config.EncodeWindowEnabled)
        {
            return "Any time";
        }

        if (!TryParseHm(config.EncodeWindowStart, out _) || !TryParseHm(config.EncodeWindowEnd, out _))
        {
            return "Invalid encode window";
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
        return wanted.Contains((int)day);
    }

    private static bool TryParseHm(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(':');
        if (parts.Length != 2
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
