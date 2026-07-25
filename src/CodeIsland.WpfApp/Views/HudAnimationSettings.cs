using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodeIsland.WpfApp.Views;

internal readonly record struct HudAnimationSettings(
    Duration SurfaceDuration,
    Duration PendingDuration,
    Duration ContentDuration,
    bool AllowsShellMorph,
    bool AllowsContentMotion,
    bool UsesSnapshotLayerForShrink,
    double ContentSlideOffset)
{
    public static HudAnimationSettings Default { get; } = new(
        new Duration(TimeSpan.FromMilliseconds(260)),
        new Duration(TimeSpan.FromMilliseconds(220)),
        new Duration(TimeSpan.FromMilliseconds(180)),
        AllowsShellMorph: true,
        AllowsContentMotion: true,
        UsesSnapshotLayerForShrink: false,
        ContentSlideOffset: 0d);

    public static HudAnimationSettings LowTierRenderer { get; } = new(
        new Duration(TimeSpan.FromMilliseconds(210)),
        new Duration(TimeSpan.FromMilliseconds(170)),
        new Duration(TimeSpan.FromMilliseconds(130)),
        AllowsShellMorph: true,
        AllowsContentMotion: true,
        UsesSnapshotLayerForShrink: false,
        ContentSlideOffset: 0d);

    public static HudAnimationSettings ForCurrentRenderer()
    {
        var tier = RenderCapability.Tier >> 16;
        return tier >= 2 ? Default : LowTierRenderer;
    }
}

public static class HudAnimationTimings
{
    public const double InlineSessionDetailHeight = 186d;

    private static HudAnimationSettings Current => HudAnimationSettings.ForCurrentRenderer();

    public static Duration SurfaceDuration => Current.SurfaceDuration;

    public static Duration ContentDuration => Current.ContentDuration;

    public static double ContentSlideOffset => Current.ContentSlideOffset;

    public static double ListItemExitSlideOffset => -Current.ContentSlideOffset * 0.45d;

    public static TimeSpan ContentSlideInDelay => TimeSpan.FromMilliseconds(30);

    public static TimeSpan ContentFadeInDelay => TimeSpan.FromMilliseconds(45);

    public static TimeSpan ContentFadeOutDelay => TimeSpan.Zero;
}

public sealed class HudShellMorphEase : EasingFunctionBase
{
    private const double Response = 8.5d;
    private static readonly double EndValue = Calculate(1d);

    public HudShellMorphEase()
    {
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime <= 0d)
            return 0d;
        if (normalizedTime >= 1d)
            return 1d;

        return Math.Clamp(Calculate(normalizedTime) / EndValue, 0d, 1d);
    }

    protected override Freezable CreateInstanceCore() => new HudShellMorphEase();

    private static double Calculate(double time) =>
        1d - (1d + Response * time) * Math.Exp(-Response * time);
}
