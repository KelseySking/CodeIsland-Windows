namespace CodeIsland.WpfApp.Services;

public static class WpfHudDensityMode
{
    public const string Classic = "classic";
    public const string Compact = "compact";
    public const string Orb = "orb";
    public const string Default = Classic;

    public const string OrbLeftKey = "orb_left";
    public const string OrbTopKey = "orb_top";
    public const string OrbMonitorIdKey = "orb_monitor_id";

    public static string Normalize(string? value) => value switch
    {
        Compact => Compact,
        Orb => Orb,
        _ => Classic
    };

    public static bool IsCompact(string? value) =>
        string.Equals(Normalize(value), Compact, StringComparison.Ordinal);

    public static bool IsOrb(string? value) =>
        string.Equals(Normalize(value), Orb, StringComparison.Ordinal);

    public static bool UsesCompactExpandedMetrics(string? value)
    {
        var mode = Normalize(value);
        return mode is Compact or Orb;
    }
}
