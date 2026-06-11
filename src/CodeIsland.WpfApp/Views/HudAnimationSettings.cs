using System.Windows;
using System.Windows.Media;

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
        UsesSnapshotLayerForShrink: true,
        ContentSlideOffset: 10d);

    public static HudAnimationSettings LowTierRenderer { get; } = new(
        new Duration(TimeSpan.FromMilliseconds(210)),
        new Duration(TimeSpan.FromMilliseconds(170)),
        new Duration(TimeSpan.FromMilliseconds(130)),
        AllowsShellMorph: true,
        AllowsContentMotion: true,
        UsesSnapshotLayerForShrink: true,
        ContentSlideOffset: 6d);

    public static HudAnimationSettings ForCurrentRenderer()
    {
        var tier = RenderCapability.Tier >> 16;
        return tier >= 2 ? Default : LowTierRenderer;
    }
}

public static class HudAnimationTimings
{
    private static HudAnimationSettings Current => HudAnimationSettings.ForCurrentRenderer();

    public static Duration SurfaceDuration => Current.SurfaceDuration;

    public static Duration ContentDuration => Current.ContentDuration;

    public static TimeSpan ContentFadeInDelay => TimeSpan.FromMilliseconds(45);

    public static TimeSpan ContentFadeOutTrailingDelay
    {
        get
        {
            var surfaceDuration = SurfaceDuration;
            var contentDuration = ContentDuration;
            if (!surfaceDuration.HasTimeSpan || !contentDuration.HasTimeSpan)
                return TimeSpan.Zero;

            return surfaceDuration.TimeSpan > contentDuration.TimeSpan
                ? surfaceDuration.TimeSpan - contentDuration.TimeSpan
                : TimeSpan.Zero;
        }
    }
}
