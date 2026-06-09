namespace CodeIsland.WpfApp.Services;

public static class WpfHudDensityMode
{
    public const string Classic = "classic";
    public const string Compact = "compact";
    public const string Default = Classic;

    public static string Normalize(string? value) =>
        string.Equals(value, Compact, StringComparison.OrdinalIgnoreCase) ? Compact : Classic;

    public static bool IsCompact(string? value) =>
        string.Equals(Normalize(value), Compact, StringComparison.Ordinal);
}
