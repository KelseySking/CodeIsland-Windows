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
