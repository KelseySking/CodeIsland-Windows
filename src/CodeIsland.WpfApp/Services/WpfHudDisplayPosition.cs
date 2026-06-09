namespace CodeIsland.WpfApp.Services;

public static class WpfHudDisplayPosition
{
    public const string TopCenter = "top-center";
    public const string MiddleLeft = "middle-left";
    public const string MiddleRight = "middle-right";
    public const string BottomCenter = "bottom-center";
    public const string Default = BottomCenter;

    public static string Normalize(string? value) => value switch
    {
        TopCenter => TopCenter,
        MiddleLeft => MiddleLeft,
        MiddleRight => MiddleRight,
        BottomCenter => BottomCenter,
        _ => Default
    };

    public static bool IsSideCenter(string? value) => Normalize(value) is MiddleLeft or MiddleRight;
}
