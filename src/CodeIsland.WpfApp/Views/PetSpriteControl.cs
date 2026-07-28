using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodeIsland.WpfApp.Models;
using CodeIsland.WpfApp.Services;

namespace CodeIsland.WpfApp.Views;

public sealed class PetSpriteControl : Border
{
    public const int FrameWidth = 192;
    public const int FrameHeight = 208;
    private const int AtlasColumns = 8;
    private const int AtlasV1Rows = 9;
    private const int AtlasV2Rows = 11;
    private const int AtlasWidth = 1536;
    private const int AtlasV1Height = 1872;
    private const int AtlasV2Height = 2288;
    private const double LookDeadzone = 18d;
    private static readonly TimeSpan LookInterval = TimeSpan.FromMilliseconds(80);
    private static readonly AnimationRow[] Rows =
    [
        new(6, [280, 110, 110, 140, 140, 320]),
        new(8, [120, 120, 120, 120, 120, 120, 120, 220]),
        new(8, [120, 120, 120, 120, 120, 120, 120, 220]),
        new(4, [140, 140, 140, 280]),
        new(5, [140, 140, 140, 140, 280]),
        new(8, [140, 140, 140, 140, 140, 140, 140, 240]),
        new(6, [150, 150, 150, 150, 150, 260]),
        new(6, [120, 120, 120, 120, 120, 220]),
        new(6, [150, 150, 150, 150, 150, 280])
    ];

    public static readonly DependencyProperty AtlasPathProperty = DependencyProperty.Register(
        nameof(AtlasPath),
        typeof(string),
        typeof(PetSpriteControl),
        new PropertyMetadata(null, OnAtlasPathChanged));

    public static readonly DependencyProperty SessionStatusProperty = DependencyProperty.Register(
        nameof(SessionStatus),
        typeof(AgentStatus),
        typeof(PetSpriteControl),
        new PropertyMetadata(AgentStatus.Idle, OnSessionStatusChanged));

    private readonly DispatcherTimer _timer = new();
    private ImageBrush? _atlasBrush;
    private byte[]? _bgraPixels;
    private int _atlasRows;
    private int _row;
    private int _column;
    private int? _dragRow;
    private int? _interactionRow;
    private Action? _interactionCompleted;
    private DateTime _nextFrameAt = DateTime.MaxValue;
    private DateTime _nextLookAt = DateTime.MaxValue;

    public PetSpriteControl()
    {
        Width = FrameWidth;
        Height = FrameHeight;
        Background = System.Windows.Media.Brushes.Transparent;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AssertMappings();
    }

    public string? AtlasPath
    {
        get => (string?)GetValue(AtlasPathProperty);
        set => SetValue(AtlasPathProperty, value);
    }

    public AgentStatus SessionStatus
    {
        get => (AgentStatus)GetValue(SessionStatusProperty);
        set => SetValue(SessionStatusProperty, value);
    }

    public bool IsAtlasLoaded => _bgraPixels is not null;

    public void ReloadAtlas() => LoadAtlas(AtlasPath);

    public void PlayWave(Action completed) => PlayOnce(3, completed);

    public void PlayJump(Action completed) => PlayOnce(4, completed);

    public void SetDragDirection(double horizontalDelta)
    {
        if (Math.Abs(horizontalDelta) < double.Epsilon)
            return;
        var row = horizontalDelta > 0 ? 1 : 2;
        if (_dragRow == row)
            return;
        _dragRow = row;
        _interactionRow = null;
        _interactionCompleted = null;
        ApplyEffectiveState(resetFrame: true);
    }

    public void EndDrag()
    {
        if (_dragRow is null)
            return;
        _dragRow = null;
        ApplyEffectiveState(resetFrame: true);
    }

    public bool IsVisiblePixelAt(System.Windows.Point localPoint)
    {
        if (_bgraPixels is null || localPoint.X < 0 || localPoint.Y < 0 ||
            localPoint.X >= ActualWidth || localPoint.Y >= ActualHeight || ActualWidth <= 0 || ActualHeight <= 0)
            return false;

        var x = Math.Clamp((int)(localPoint.X / ActualWidth * FrameWidth), 0, FrameWidth - 1);
        var y = Math.Clamp((int)(localPoint.Y / ActualHeight * FrameHeight), 0, FrameHeight - 1);
        var atlasX = _column * FrameWidth + x;
        var atlasY = _row * FrameHeight + y;
        return _bgraPixels[(atlasY * AtlasWidth + atlasX) * 4 + 3] > 8;
    }

    private static void OnAtlasPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PetSpriteControl)d).LoadAtlas(e.NewValue as string);

    private static void OnSessionStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PetSpriteControl)d;
        control.ApplyEffectiveState(resetFrame: control._dragRow is null && control._interactionRow is null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged += OnAnimationCapabilityChanged;
        RenderCapability.TierChanged += OnAnimationCapabilityChanged;
        if (_bgraPixels is null)
            LoadAtlas(AtlasPath);
        ApplyEffectiveState(resetFrame: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnAnimationCapabilityChanged;
        RenderCapability.TierChanged -= OnAnimationCapabilityChanged;
        _timer.Stop();
        _dragRow = null;
        _interactionRow = null;
        _interactionCompleted = null;
    }

    private void OnAnimationCapabilityChanged(object? sender, EventArgs e)
    {
        if (!CanAnimate && _interactionRow is not null)
        {
            var completed = _interactionCompleted;
            _interactionRow = null;
            _interactionCompleted = null;
            ApplyEffectiveState(resetFrame: true);
            if (completed is not null)
                Dispatcher.BeginInvoke(completed, DispatcherPriority.Input);
            return;
        }

        ApplyEffectiveState(resetFrame: true);
    }

    private bool CanAnimate =>
        IsLoaded && PresentationSource.FromVisual(this) is not null &&
        SystemParameters.ClientAreaAnimation && (RenderCapability.Tier >> 16) >= 2;

    private void LoadAtlas(string? path)
    {
        _timer.Stop();
        _atlasBrush = null;
        _bgraPixels = null;
        _atlasRows = 0;
        Background = System.Windows.Media.Brushes.Transparent;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            var decoded = WpfPetAtlasDecoder.Decode(path);
            if (decoded.Bitmap.PixelWidth != AtlasWidth)
                return;
            var atlasRows = ResolveAtlasRows(decoded.Bitmap.PixelHeight);
            if (atlasRows == 0)
                return;

            _bgraPixels = decoded.BgraPixels;
            _atlasRows = atlasRows;
            _atlasBrush = new ImageBrush(decoded.Bitmap)
            {
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Stretch = Stretch.Fill,
                TileMode = TileMode.None
            };
            Background = _atlasBrush;
            ApplyEffectiveState(resetFrame: true);
        }
        catch
        {
            _atlasBrush = null;
            _bgraPixels = null;
            _atlasRows = 0;
            Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void PlayOnce(int row, Action completed)
    {
        if (_interactionRow == row)
            return;
        _dragRow = null;
        _interactionRow = row;
        _interactionCompleted = completed;
        ApplyEffectiveState(resetFrame: true);
        if (CanAnimate && _bgraPixels is not null)
            return;

        _interactionRow = null;
        _interactionCompleted = null;
        ApplyEffectiveState(resetFrame: true);
        Dispatcher.BeginInvoke(completed, DispatcherPriority.Input);
    }

    private void ApplyEffectiveState(bool resetFrame)
    {
        if (_bgraPixels is null)
        {
            _timer.Stop();
            return;
        }

        var row = _dragRow ?? _interactionRow ?? ResolveStatusRow(SessionStatus);
        if (!CanAnimate)
        {
            SetFrame(row, 0);
            _timer.Stop();
            return;
        }

        if (_atlasRows == AtlasV2Rows && row == 0 && _dragRow is null && _interactionRow is null)
        {
            _nextLookAt = DateTime.UtcNow;
            UpdateLookDirection();
            if (_row is 9 or 10)
            {
                _nextFrameAt = DateTime.MaxValue;
                ScheduleTimer();
                return;
            }
        }
        else
        {
            _nextLookAt = DateTime.MaxValue;
        }

        if (resetFrame || _row != row || _row is 9 or 10)
        {
            SetFrame(row, 0);
            _nextFrameAt = DateTime.UtcNow.AddMilliseconds(Rows[row].Durations[0]);
        }
        ScheduleTimer();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!CanAnimate)
        {
            ApplyEffectiveState(resetFrame: true);
            return;
        }

        var now = DateTime.UtcNow;
        if (now >= _nextLookAt)
        {
            _nextLookAt = now + LookInterval;
            UpdateLookDirection();
        }

        if (now >= _nextFrameAt && _row <= 8)
            AdvanceFrame(now);

        ScheduleTimer();
    }

    private void AdvanceFrame(DateTime now)
    {
        var animation = Rows[_row];
        if (_column + 1 < animation.FrameCount)
        {
            SetFrame(_row, _column + 1);
            _nextFrameAt = now.AddMilliseconds(animation.Durations[_column]);
            return;
        }

        if (_interactionRow == _row)
        {
            var completed = _interactionCompleted;
            _interactionRow = null;
            _interactionCompleted = null;
            ApplyEffectiveState(resetFrame: true);
            completed?.Invoke();
            return;
        }

        SetFrame(_row, 0);
        _nextFrameAt = now.AddMilliseconds(animation.Durations[0]);
    }

    private void UpdateLookDirection()
    {
        if (_atlasRows != AtlasV2Rows || _dragRow is not null || _interactionRow is not null || ResolveStatusRow(SessionStatus) != 0 || !GetCursorPos(out var cursor))
        {
            _nextLookAt = DateTime.MaxValue;
            return;
        }

        var point = PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        var dx = point.X - (ActualWidth > 0 ? ActualWidth : FrameWidth) / 2d;
        var dy = point.Y - (ActualHeight > 0 ? ActualHeight : FrameHeight) / 2d;
        var look = ResolveLookCell(dx, dy);
        if (look is null)
        {
            if (_row is 9 or 10)
            {
                SetFrame(0, 0);
                _nextFrameAt = DateTime.UtcNow.AddMilliseconds(Rows[0].Durations[0]);
            }
            return;
        }

        SetFrame(look.Value.Row, look.Value.Column);
        _nextFrameAt = DateTime.MaxValue;
    }

    private void ScheduleTimer()
    {
        if (!CanAnimate)
        {
            _timer.Stop();
            return;
        }

        var next = _nextFrameAt < _nextLookAt ? _nextFrameAt : _nextLookAt;
        if (next == DateTime.MaxValue)
        {
            _timer.Stop();
            return;
        }

        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp((next - DateTime.UtcNow).TotalMilliseconds, 10d, 1000d));
        _timer.Start();
    }

    private void SetFrame(int row, int column)
    {
        _row = row;
        _column = column;
        if (_atlasBrush is not null)
            _atlasBrush.Viewbox = GetFrameViewbox(row, column, _atlasRows);
    }

    private static int ResolveAtlasRows(int pixelHeight) => pixelHeight switch
    {
        AtlasV1Height => AtlasV1Rows,
        AtlasV2Height => AtlasV2Rows,
        _ => 0
    };

    private static Rect GetFrameViewbox(int row, int column, int atlasRows) =>
        new(column / (double)AtlasColumns, row / (double)atlasRows, 1d / AtlasColumns, 1d / atlasRows);

    private static int ResolveStatusRow(AgentStatus status) => status switch
    {
        AgentStatus.Error => 5,
        AgentStatus.WaitingApproval or AgentStatus.WaitingQuestion => 6,
        AgentStatus.Processing or AgentStatus.Running => 7,
        AgentStatus.Completed => 8,
        _ => 0
    };

    private static (int Row, int Column)? ResolveLookCell(double dx, double dy)
    {
        if (Math.Sqrt(dx * dx + dy * dy) < LookDeadzone)
            return null;
        var degrees = Math.Atan2(dx, -dy) * 180d / Math.PI;
        if (degrees < 0)
            degrees += 360d;
        var index = (int)Math.Round(degrees / 22.5d, MidpointRounding.AwayFromZero) % 16;
        return (9 + index / 8, index % 8);
    }

    [Conditional("DEBUG")]
    private static void AssertMappings()
    {
        Debug.Assert(ResolveStatusRow(AgentStatus.Idle) == 0);
        Debug.Assert(ResolveStatusRow(AgentStatus.Error) == 5);
        Debug.Assert(ResolveStatusRow(AgentStatus.WaitingApproval) == 6);
        Debug.Assert(ResolveStatusRow(AgentStatus.Running) == 7);
        Debug.Assert(ResolveStatusRow(AgentStatus.Completed) == 8);
        Debug.Assert(ResolveLookCell(0, -100) == (9, 0));
        Debug.Assert(ResolveLookCell(100, 0) == (9, 4));
        Debug.Assert(ResolveLookCell(0, 100) == (10, 0));
        Debug.Assert(ResolveLookCell(-100, 0) == (10, 4));
        Debug.Assert(ResolveLookCell(0, 0) is null);
        Debug.Assert(AtlasWidth == FrameWidth * AtlasColumns);
        Debug.Assert(AtlasV1Height == FrameHeight * AtlasV1Rows);
        Debug.Assert(AtlasV2Height == FrameHeight * AtlasV2Rows);
        Debug.Assert(ResolveAtlasRows(AtlasV1Height) == AtlasV1Rows);
        Debug.Assert(ResolveAtlasRows(AtlasV2Height) == AtlasV2Rows);
        Debug.Assert(ResolveAtlasRows(2000) == 0);
        foreach (var rows in new[] { AtlasV1Rows, AtlasV2Rows })
        {
            Debug.Assert(GetFrameViewbox(0, 0, rows) == new Rect(0d, 0d, 1d / AtlasColumns, 1d / rows));
            var lastViewbox = GetFrameViewbox(rows - 1, AtlasColumns - 1, rows);
            Debug.Assert(Math.Abs(lastViewbox.Right - 1d) < 1e-12);
            Debug.Assert(Math.Abs(lastViewbox.Bottom - 1d) < 1e-12);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private sealed record AnimationRow(int FrameCount, int[] Durations);
}
