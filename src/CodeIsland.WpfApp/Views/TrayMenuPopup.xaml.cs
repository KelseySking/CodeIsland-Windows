using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodeIsland.WpfApp.Services;

namespace CodeIsland.WpfApp.Views;

public partial class TrayMenuPopup : Window
{
    private readonly Action _onClosedByUser;
    private bool _closing;

    public TrayMenuPopup(
        bool isHudVisible,
        bool isHudExpanded,
        bool runtimeConnected,
        string runtimeStatusText,
        Action showOrHideHud,
        Action toggleExpanded,
        Action showSettings,
        Action showAbout,
        Action exit,
        Action onClosedByUser)
    {
        InitializeComponent();
        _onClosedByUser = onClosedByUser;

        VisibilityItem.Content = isHudVisible ? "隐藏 HUD" : "显示 HUD";
        ExpandItem.Content = isHudExpanded ? "收起 HUD" : "展开 HUD";
        RuntimeStatusText.Text = runtimeStatusText;
        RuntimeDot.Fill = runtimeConnected
            ? (System.Windows.Media.Brush)FindResource("HudAccentBrush")
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));

        VisibilityItem.Click += (_, _) => RunAndClose(showOrHideHud);
        ExpandItem.Click += (_, _) => RunAndClose(toggleExpanded);
        SettingsItem.Click += (_, _) => RunAndClose(showSettings);
        AboutItem.Click += (_, _) => RunAndClose(showAbout);
        ExitItem.Click += (_, _) => RunAndClose(exit);

        Loaded += OnLoaded;
        Deactivated += (_, _) => CloseMenu();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ShowNearCursor()
    {
        // Forms mouse position is device pixels; convert after Show when HWND/DPI is known.
        var devicePoint = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(devicePoint);

        // Rough pre-position (may be device px); refined immediately after Show.
        Left = devicePoint.X - 8;
        Top = devicePoint.Y - 220;

        Show();
        Activate();
        PositionNearDevicePoint(devicePoint, screen);
        VisibilityItem.Focus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var devicePoint = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(devicePoint);
        PositionNearDevicePoint(devicePoint, screen);
        PlayOpenAnimation();
    }

    private void PositionNearDevicePoint(System.Drawing.Point devicePoint, System.Windows.Forms.Screen screen)
    {
        UpdateLayout();
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        var anchor = fromDevice.Transform(new System.Windows.Point(devicePoint.X, devicePoint.Y));
        var workTopLeft = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var workBottomRight = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 220;

        var left = anchor.X - 8;
        var top = anchor.Y - height - 8;
        if (left + width > workBottomRight.X)
            left = workBottomRight.X - width - 8;
        if (left < workTopLeft.X)
            left = workTopLeft.X + 8;
        if (top < workTopLeft.Y)
            top = anchor.Y + 8;
        if (top + height > workBottomRight.Y)
            top = workBottomRight.Y - height - 8;

        Left = left;
        Top = top;
    }

    private void PlayOpenAnimation()
    {
        var settings = HudAnimationSettings.ForCurrentRenderer();
        var duration = new Duration(TimeSpan.FromMilliseconds(settings.AllowsContentMotion ? 150 : 90));
        var slide = settings.AllowsContentMotion ? 8d : 0d;

        // Animate the chrome, not the Window — Window.Opacity=0 would keep the menu invisible.
        MenuRoot.BeginAnimation(OpacityProperty, null);
        MenuRoot.Opacity = 0;
        if (MenuRoot.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
        {
            translate = new TranslateTransform(0, slide);
            MenuRoot.RenderTransform = translate;
        }
        else
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = slide;
        }

        MenuRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        if (slide > 0)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slide, 0, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseMenu();
            return;
        }

        var buttons = GetMenuButtons();
        var current = buttons.FindIndex(b => b.IsKeyboardFocusWithin || b.IsFocused);
        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            if (buttons.Count == 0)
                return;
            var next = e.Key == Key.Down
                ? (current < 0 ? 0 : (current + 1) % buttons.Count)
                : (current <= 0 ? buttons.Count - 1 : current - 1);
            buttons[next].Focus();
            return;
        }

        if (e.Key == Key.Enter && current >= 0)
        {
            e.Handled = true;
            buttons[current].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
    }

    private List<System.Windows.Controls.Button> GetMenuButtons() =>
    [
        VisibilityItem,
        ExpandItem,
        SettingsItem,
        AboutItem,
        ExitItem
    ];

    private void RunAndClose(Action action)
    {
        try
        {
            action();
        }
        finally
        {
            CloseMenu();
        }
    }

    private void CloseMenu()
    {
        if (_closing)
            return;
        _closing = true;
        try
        {
            Close();
        }
        catch
        {
            // ignore close races
        }
        finally
        {
            _onClosedByUser();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Keep topmost tray popup without stealing permanent focus from other apps longer than needed.
        var hwnd = new WindowInteropHelper(this).Handle;
        const int gwlExStyle = -20;
        const int wsExToolwindow = 0x00000080;
        var style = GetWindowLong(hwnd, gwlExStyle);
        SetWindowLong(hwnd, gwlExStyle, style | wsExToolwindow);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
