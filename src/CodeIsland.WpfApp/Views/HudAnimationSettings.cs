using System.Windows;

namespace CodeIsland.WpfApp.Views;

internal readonly record struct HudAnimationSettings(
    Duration SurfaceDuration,
    Duration PendingDuration,
    Duration ContentDuration)
{
    public bool AllowsShellMorph => true;

    public bool AllowsContentMotion => true;

    public static HudAnimationSettings Default { get; } = new(
        new Duration(TimeSpan.FromMilliseconds(260)),
        new Duration(TimeSpan.FromMilliseconds(220)),
        new Duration(TimeSpan.FromMilliseconds(180)));
}
