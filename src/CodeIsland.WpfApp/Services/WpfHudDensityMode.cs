namespace CodeIsland.WpfApp.Services;

public static class WpfHudDensityMode
{
    public const string Classic = "classic";
    public const string Compact = "compact";
    public const string Orb = "orb";
    public const string Pet = "pet";
    public const string Default = Classic;

    public const string OrbLeftKey = "orb_left";
    public const string OrbTopKey = "orb_top";
    public const string OrbMonitorIdKey = "orb_monitor_id";

    public const string PetScalePercentKey = "pet_scale_percent";
    public const double PetScalePercentMinimum = 50d;
    public const double PetScalePercentDefault = 100d;
    public const double PetScalePercentMaximum = 200d;
    public const double PetScalePercentStep = 10d;

    public static string Normalize(string? value) => value switch
    {
        Compact => Compact,
        Orb => Orb,
        Pet => Pet,
        _ => Classic
    };

    public static bool IsCompact(string? value) =>
        string.Equals(Normalize(value), Compact, StringComparison.Ordinal);

    public static bool IsOrb(string? value) =>
        string.Equals(Normalize(value), Orb, StringComparison.Ordinal);

    public static bool IsPet(string? value) =>
        string.Equals(Normalize(value), Pet, StringComparison.Ordinal);

    public static bool UsesFloatingAnchor(string? value)
    {
        var mode = Normalize(value);
        return mode is Orb or Pet;
    }

    public static bool UsesCompactExpandedMetrics(string? value)
    {
        var mode = Normalize(value);
        return mode is Compact or Orb or Pet;
    }

    public static double NormalizePetScalePercent(double value)
    {
        if (!double.IsFinite(value))
            return PetScalePercentDefault;

        var snapped = Math.Round(value / PetScalePercentStep, MidpointRounding.AwayFromZero) * PetScalePercentStep;
        return Math.Clamp(snapped, PetScalePercentMinimum, PetScalePercentMaximum);
    }
}
