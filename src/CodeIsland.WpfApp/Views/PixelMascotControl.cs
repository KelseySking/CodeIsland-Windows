using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeIsland.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;

namespace CodeIsland.WpfApp.Views;

public sealed class PixelMascotControl : Control
{
    public static readonly DependencyProperty PixelSizeProperty = DependencyProperty.Register(
        nameof(PixelSize),
        typeof(double),
        typeof(PixelMascotControl),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SessionStatusProperty = DependencyProperty.Register(
        nameof(SessionStatus),
        typeof(AgentStatus),
        typeof(PixelMascotControl),
        new FrameworkPropertyMetadata(AgentStatus.Idle, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SessionSourceProperty = DependencyProperty.Register(
        nameof(SessionSource),
        typeof(string),
        typeof(PixelMascotControl),
        new FrameworkPropertyMetadata("codeisland", FrameworkPropertyMetadataOptions.AffectsRender));

    private const int SpriteColumns = 11;
    private const int SpriteRows = 13;
    private const int ImagePixelSize = 256;
    private static readonly Dictionary<string, BitmapImage?> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Color BodyColor = Color.FromRgb(0xDE, 0x88, 0x6D);
    private static readonly Color EyeColor = Color.FromRgb(0x2D, 0x2D, 0x2D);
    private static readonly Color HighlightColor = Color.FromRgb(0xFF, 0xD4, 0xC4);
    private static readonly Color MouthColor = Color.FromRgb(0xB8, 0x5C, 0x4A);
    private static readonly Brush[] Palette =
    [
        Brushes.Transparent,
        Freeze(new SolidColorBrush(BodyColor)),
        Freeze(new SolidColorBrush(EyeColor)),
        Freeze(new SolidColorBrush(HighlightColor)),
        Freeze(new SolidColorBrush(MouthColor))
    ];

    private static readonly int[,] IdleSprite =
    {
        { 0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0 },
        { 0, 0, 1, 3, 3, 1, 1, 1, 1, 0, 0 },
        { 0, 1, 3, 3, 1, 1, 1, 1, 1, 1, 0 },
        { 0, 1, 3, 1, 1, 1, 1, 1, 1, 1, 0 },
        { 1, 1, 1, 1, 2, 1, 1, 2, 1, 1, 1 },
        { 1, 1, 1, 1, 2, 1, 1, 2, 1, 1, 1 },
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
        { 1, 1, 1, 1, 1, 4, 4, 1, 1, 1, 1 },
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
        { 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0 },
        { 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0 },
        { 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0 },
        { 0, 1, 1, 0, 0, 0, 0, 0, 1, 1, 0 },
    };

    private static readonly int[,] WorkingSprite =
    {
        { 0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0 },
        { 0, 0, 1, 3, 3, 1, 1, 1, 1, 0, 0 },
        { 0, 1, 3, 3, 1, 1, 1, 1, 1, 1, 0 },
        { 0, 1, 3, 1, 1, 1, 1, 1, 1, 1, 0 },
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
        { 1, 1, 1, 2, 2, 1, 2, 2, 1, 1, 1 },
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
        { 1, 1, 1, 1, 1, 4, 4, 1, 1, 1, 1 },
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
        { 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0 },
        { 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0 },
        { 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0 },
        { 0, 1, 1, 0, 0, 0, 0, 0, 1, 1, 0 },
    };

    private static readonly int[,] WaitingSprite =
    {
        { 0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0 },
        { 0, 0, 1, 3, 3, 1, 1, 1, 1, 0, 0 },
        { 0, 1, 3, 3, 1, 1, 1, 1, 1, 1, 0 },
        { 0, 1, 3, 1, 1, 1, 1, 1, 1, 1, 0 },
        { 1, 1, 1, 1, 2, 1, 1, 2, 1, 1, 1 },
        { 1, 1, 1, 1, 2, 1, 1, 2, 1, 1, 1 },
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
        { 1, 1, 1, 1, 4, 0, 0, 4, 1, 1, 1 },
        { 1, 1, 1, 1, 1, 4, 4, 1, 1, 1, 1 },
        { 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0 },
        { 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0 },
        { 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0 },
        { 0, 1, 1, 0, 0, 0, 0, 0, 1, 1, 0 },
    };

    public double PixelSize
    {
        get => (double)GetValue(PixelSizeProperty);
        set => SetValue(PixelSizeProperty, value);
    }

    public AgentStatus SessionStatus
    {
        get => (AgentStatus)GetValue(SessionStatusProperty);
        set => SetValue(SessionStatusProperty, value);
    }

    public string SessionSource
    {
        get => (string)GetValue(SessionSourceProperty);
        set => SetValue(SessionSourceProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var pixelSize = PixelSize;
        return new Size(11 * pixelSize, 13 * pixelSize);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (TryResolveImage(SessionSource, SessionStatus) is { } image)
        {
            var side = Math.Min(ActualWidth, ActualHeight);
            if (side > 0)
            {
                var x = Math.Round((ActualWidth - side) / 2.0);
                var y = Math.Round((ActualHeight - side) / 2.0);
                drawingContext.DrawImage(image, new Rect(x, y, side, side));
                return;
            }
        }

        var sprite = ResolveSprite(SessionStatus);
        var pixelSize = PixelSize;
        var rows = sprite.GetLength(0);
        var columns = sprite.GetLength(1);
        var offsetX = Math.Round((ActualWidth - columns * pixelSize) / 2.0);
        var offsetY = Math.Round((ActualHeight - rows * pixelSize) / 2.0);

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var colorIndex = sprite[row, column];
                if (colorIndex == 0)
                    continue;

                drawingContext.DrawRectangle(
                    Palette[colorIndex],
                    null,
                    new Rect(offsetX + column * pixelSize, offsetY + row * pixelSize, pixelSize, pixelSize));
            }
        }
    }

    private static int[,] ResolveSprite(AgentStatus status) => status switch
    {
        AgentStatus.Processing or AgentStatus.Running => WorkingSprite,
        AgentStatus.WaitingApproval or AgentStatus.WaitingQuestion => WaitingSprite,
        _ => IdleSprite
    };

    private static BitmapImage? TryResolveImage(string? source, AgentStatus status)
    {
        var path = ResolveImagePath(source, status);
        if (path == null)
            return null;

        if (ImageCache.TryGetValue(path, out var cached))
            return cached;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/{path}", UriKind.Absolute);
            image.DecodePixelWidth = ImagePixelSize;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            ImageCache[path] = image;
            return image;
        }
        catch
        {
            ImageCache[path] = null;
            return null;
        }
    }

    private static string ResolveImagePath(string? source, AgentStatus status)
    {
        var state = status switch
        {
            AgentStatus.Processing or AgentStatus.Running => "working",
            AgentStatus.WaitingApproval or AgentStatus.WaitingQuestion => "waiting-confirmation",
            _ => "idle"
        };
        return $"Assets/codeisland-status-assets/{state}.png";
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
