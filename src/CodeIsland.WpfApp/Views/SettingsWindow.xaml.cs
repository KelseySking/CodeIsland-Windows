using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodeIsland.Contracts;
using CodeIsland.WpfApp.Services;
using CodeIsland.WpfApp.ViewModels;

namespace CodeIsland.WpfApp.Views;

public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private const string GitHubReleasesUrl = "https://github.com/KelseySking/CodeOrbit-Rust/releases";
    private const string AboutSectionId = "about";
    private const string RecordIdleLabel = "点击录制";
    private const string RecordActiveLabel = "按下组合键…";

    private readonly SettingsManager _settings;
    private readonly IWpfSourceService _sourceService;
    private readonly Dictionary<string, FrameworkElement> _sections = new(StringComparer.Ordinal);
    private string _activeSectionId = "general";
    private string? _recordingHotkeyField;
    private bool _sectionTransitionInProgress;
    private bool _autoApproveSafeTools;
    private bool _autoApproveAllPermissions;
    private bool _hideWhenFullscreen;
    private bool _launchAtLogin;
    private string _displayPosition = WpfHudDisplayPosition.Default;
    private string _displayMonitor = WpfMonitorService.AutoMonitorId;
    private string _hudDensityMode = WpfHudDensityMode.Default;
    private string _panelHeightMode = "auto";
    private string _webhookUrl = "";
    private string _sessionTimeoutSeconds = "300";
    private string _togglePanelHotkey = "Ctrl+Alt+I";
    private string _approveHotkey = "Ctrl+Alt+Y";
    private string _denyHotkey = "Ctrl+Alt+N";
    private bool _soundEnabled = true;
    private bool _smartSoundSuppression = true;
    private bool _showFullRecentMessages;
    private double _volumePercent = 70;
    private string _feedbackText = "设置会自动保存。";

    // Runtime 状态栏
    private string _runtimeConnectionStatus = "CodeOrbit 连接中...";
    private string _runtimeVersion = "";
    private System.Windows.Media.Brush _runtimeStatusColor = System.Windows.Media.Brushes.Gray;

    // 工具列表
    private bool _sourcesLoaded;
    private bool _wslAvailable;
    private bool _suppressWslDistroRefresh;
    private string? _selectedWslDistro;
    private ObservableCollection<WslDistroItem> _wslDistros = new();
    private int _wslLoadGeneration;
    private static readonly TimeSpan WslListTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WslStatusTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WslOperationTimeout = TimeSpan.FromSeconds(60);


    // CodeOrbit 版本信息
    private string _runtimeProduct = "";
    private string _currentVersion = "";

    public SettingsWindow(SettingsManager settings, IWpfSourceService? sourceService = null)
    {
        InitializeComponent();
        _settings = settings;
        _sourceService = sourceService ?? new UnavailableWpfSourceService();
        _autoApproveSafeTools = _settings.Get("auto_approve_safe_tools", false);
        _autoApproveAllPermissions = _settings.Get(SettingsManager.AutoApproveAllPermissionsKey, false);
        _hideWhenFullscreen = _settings.Get("hide_when_fullscreen", true);
        _launchAtLogin = WpfStartupManager.IsEnabled();
        _displayPosition = ReadDisplayPosition();
        _displayMonitor = _settings.Get("display_monitor", WpfMonitorService.AutoMonitorId);
        _hudDensityMode = ReadHudDensityMode();
        _panelHeightMode = _settings.Get("panel_height_mode", "auto");
        _webhookUrl = _settings.Get("webhook_url", "");
        _sessionTimeoutSeconds = _settings.Get("session_timeout", 300).ToString(CultureInfo.InvariantCulture);
        _togglePanelHotkey = _settings.Get("hotkey_toggle_panel", "Ctrl+Alt+I");
        _approveHotkey = _settings.Get("hotkey_approve", "Ctrl+Alt+Y");
        _denyHotkey = _settings.Get("hotkey_deny", "Ctrl+Alt+N");
        _soundEnabled = _settings.Get("sound_enabled", true);
        _smartSoundSuppression = _settings.Get("smart_suppression", true);
        _showFullRecentMessages = _settings.Get("show_full_recent_messages", false);
        _volumePercent = Math.Clamp(_settings.Get("volume", 0.7), 0.0, 1.0) * 100.0;
        _selectedWslDistro = _settings.Get("last_wsl_distro", (string?)null);
        DataContext = this;
        RefreshMonitorOptions();
        RegisterSections();
        ShowSection(_activeSectionId, animate: false);

        // 加载 Runtime 状态
        _ = LoadRuntimeStatusAsync();

        // 加载工具列表
        _ = LoadSourcesAsync();
    }

    private void RootChrome_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 几何裁剪保证四角同一半径（纯 CornerRadius+子矩形顶栏会导致上下圆角观感不一致）
        if (RootClip is null)
            return;
        RootClip.Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
        RootClip.RadiusX = 14;
        RootClip.RadiusY = 14;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        // 点在最小化/关闭按钮上时不拖
        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) != null)
            return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 托盘打开时 Win32 常拒绝抢前台：Show + 短暂 Topmost + SetForegroundWindow。
    /// </summary>
    public void BringToFront()
    {
        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);


    public event Func<string, string, string, bool>? HotkeysChangeRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DisplayMonitorOption> DisplayMonitorOptions { get; } = new();

    // 工具列表
    public ObservableCollection<SourceViewModel> Sources { get; } = new();

    public ObservableCollection<WslDistroItem> WslDistros => _wslDistros;

    public bool WslAvailable
    {
        get => _wslAvailable;
        set
        {
            if (_wslAvailable == value) return;
            _wslAvailable = value;
            OnPropertyChanged();
        }
    }

    public string? SelectedWslDistro
    {
        get => _selectedWslDistro;
        set
        {
            if (_selectedWslDistro == value) return;
            _selectedWslDistro = value;
            OnPropertyChanged();
            if (!string.IsNullOrWhiteSpace(value))
                _settings.Set("last_wsl_distro", value);
            if (!_suppressWslDistroRefresh)
                _ = RefreshWslStatusesAsync();
        }
    }


    public bool SourcesLoaded
    {
        get => _sourcesLoaded;
        set
        {
            if (_sourcesLoaded == value) return;
            _sourcesLoaded = value;
            OnPropertyChanged();
        }
    }


    // Runtime 状态栏
    public string RuntimeConnectionStatus
    {
        get => _runtimeConnectionStatus;
        set
        {
            if (string.Equals(_runtimeConnectionStatus, value, StringComparison.Ordinal)) return;
            _runtimeConnectionStatus = value;
            OnPropertyChanged();
        }
    }

    public string RuntimeVersion
    {
        get => _runtimeVersion;
        set
        {
            if (string.Equals(_runtimeVersion, value, StringComparison.Ordinal)) return;
            _runtimeVersion = value;
            OnPropertyChanged();
        }
    }

    public System.Windows.Media.Brush RuntimeStatusColor
    {
        get => _runtimeStatusColor;
        set
        {
            if (_runtimeStatusColor == value) return;
            _runtimeStatusColor = value;
            OnPropertyChanged();
        }
    }

    // 关于页 - 更新检测
    public string RuntimeProduct
    {
        get => _runtimeProduct;
        set
        {
            if (string.Equals(_runtimeProduct, value, StringComparison.Ordinal)) return;
            _runtimeProduct = value;
            OnPropertyChanged();
        }
    }

    public string CurrentVersion
    {
        get => _currentVersion;
        set
        {
            if (string.Equals(_currentVersion, value, StringComparison.Ordinal)) return;
            _currentVersion = value;
            OnPropertyChanged();
        }
    }

    public bool AutoApproveSafeTools
    {
        get => _autoApproveSafeTools;
        set
        {
            if (_autoApproveSafeTools == value) return;
            _autoApproveSafeTools = value;
            OnPropertyChanged();
            _settings.Set("auto_approve_safe_tools", value);
            FeedbackText = value ? "安全只读工具自动审批已开启" : "安全只读工具自动审批已关闭";
        }
    }

    public bool AutoApproveAllPermissions
    {
        get => _autoApproveAllPermissions;
        set
        {
            if (_autoApproveAllPermissions == value) return;
            _autoApproveAllPermissions = value;
            OnPropertyChanged();
            _settings.Set(SettingsManager.AutoApproveAllPermissionsKey, value);
            FeedbackText = value ? "一键通过审批已开启，问答仍需手动处理" : "一键通过审批已关闭";
        }
    }

    public bool HideWhenFullscreen
    {
        get => _hideWhenFullscreen;
        set
        {
            if (_hideWhenFullscreen == value) return;
            _hideWhenFullscreen = value;
            OnPropertyChanged();
            _settings.Set("hide_when_fullscreen", value);
            FeedbackText = value ? "全屏隐藏已开启，并已重新评估 HUD 可见性" : "全屏隐藏已关闭，HUD 会立即恢复显示";
        }
    }

    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (_launchAtLogin == value) return;
            if (!WpfStartupManager.SetEnabled(value, out var message))
            {
                FeedbackText = message;
                OnPropertyChanged();
                return;
            }

            _launchAtLogin = value;
            OnPropertyChanged();
            _settings.Set("launch_at_login", value);
            FeedbackText = message;
        }
    }

    public string DisplayPosition
    {
        get => _displayPosition;
        set
        {
            var normalized = WpfHudDisplayPosition.Normalize(value);
            if (string.Equals(_displayPosition, normalized, StringComparison.Ordinal)) return;
            _displayPosition = normalized;
            OnPropertyChanged();
            _settings.Set("display_position", normalized);
            FeedbackText = "悬浮窗显示位置已更新";
        }
    }

    public string DisplayMonitor
    {
        get => _displayMonitor;
        set
        {
            if (string.Equals(_displayMonitor, value, StringComparison.Ordinal)) return;
            _displayMonitor = value;
            OnPropertyChanged();
            _settings.Set("display_monitor", value);
            FeedbackText = string.Equals(value, WpfMonitorService.AutoMonitorId, StringComparison.OrdinalIgnoreCase)
                ? "悬浮窗显示器已设为自动"
                : "悬浮窗显示器已更新";
        }
    }

    public string HudDensityMode
    {
        get => _hudDensityMode;
        set
        {
            var normalized = WpfHudDensityMode.Normalize(value);
            if (string.Equals(_hudDensityMode, normalized, StringComparison.Ordinal)) return;
            _hudDensityMode = normalized;
            OnPropertyChanged();
            _settings.Set("hud_density_mode", normalized);
            FeedbackText = normalized == WpfHudDensityMode.Compact
                ? "HUD 已切换为紧凑"
                : "HUD 已切换为经典样式";
        }
    }

    public string PanelHeightMode
    {
        get => _panelHeightMode;
        set
        {
            if (string.Equals(_panelHeightMode, value, StringComparison.Ordinal)) return;
            _panelHeightMode = value;
            OnPropertyChanged();
            _settings.Set("panel_height_mode", value);
            FeedbackText = value switch
            {
                "fixed" => "面板高度模式已设为固定",
                "compact" => "面板高度模式已设为紧凑",
                _ => "面板高度模式已设为自动"
            };
        }
    }

    public string WebhookUrl
    {
        get => _webhookUrl;
        set
        {
            if (string.Equals(_webhookUrl, value, StringComparison.Ordinal)) return;
            _webhookUrl = value;
            OnPropertyChanged();
            _settings.Set("webhook_url", value);
            FeedbackText = string.IsNullOrWhiteSpace(value)
                ? "提醒转发已关闭"
                : WpfWebhookNotifier.TryNormalizeWebhookUri(value, out _)
                    ? "提醒转发地址已保存"
                    : "提醒转发地址格式无效";
        }
    }

    public string SessionTimeoutSeconds
    {
        get => _sessionTimeoutSeconds;
        set
        {
            if (string.Equals(_sessionTimeoutSeconds, value, StringComparison.Ordinal)) return;
            _sessionTimeoutSeconds = value;
            OnPropertyChanged();
            ApplySessionTimeout(value);
        }
    }

    public string TogglePanelHotkey
    {
        get => _togglePanelHotkey;
        set
        {
            if (string.Equals(_togglePanelHotkey, value, StringComparison.Ordinal)) return;
            _togglePanelHotkey = value;
            OnPropertyChanged();
            ApplyHotkeysIfValid();
        }
    }

    public string ApproveHotkey
    {
        get => _approveHotkey;
        set
        {
            if (string.Equals(_approveHotkey, value, StringComparison.Ordinal)) return;
            _approveHotkey = value;
            OnPropertyChanged();
            ApplyHotkeysIfValid();
        }
    }

    public string DenyHotkey
    {
        get => _denyHotkey;
        set
        {
            if (string.Equals(_denyHotkey, value, StringComparison.Ordinal)) return;
            _denyHotkey = value;
            OnPropertyChanged();
            ApplyHotkeysIfValid();
        }
    }

    public string ToggleHotkeyRecordLabel =>
        string.Equals(_recordingHotkeyField, "toggle", StringComparison.Ordinal) ? RecordActiveLabel : RecordIdleLabel;

    public string ApproveHotkeyRecordLabel =>
        string.Equals(_recordingHotkeyField, "approve", StringComparison.Ordinal) ? RecordActiveLabel : RecordIdleLabel;

    public string DenyHotkeyRecordLabel =>
        string.Equals(_recordingHotkeyField, "deny", StringComparison.Ordinal) ? RecordActiveLabel : RecordIdleLabel;

    public bool SoundEnabled
    {
        get => _soundEnabled;
        set
        {
            if (_soundEnabled == value) return;
            _soundEnabled = value;
            OnPropertyChanged();
            _settings.Set("sound_enabled", value);
            FeedbackText = value ? "音效已开启" : "音效已关闭";
        }
    }

    public bool SmartSoundSuppression
    {
        get => _smartSoundSuppression;
        set
        {
            if (_smartSoundSuppression == value) return;
            _smartSoundSuppression = value;
            OnPropertyChanged();
            _settings.Set("smart_suppression", value);
            FeedbackText = value ? "重复音效抑制已开启" : "重复音效抑制已关闭";
        }
    }

    public bool ShowFullRecentMessages
    {
        get => _showFullRecentMessages;
        set
        {
            if (_showFullRecentMessages == value) return;
            _showFullRecentMessages = value;
            OnPropertyChanged();
            _settings.Set("show_full_recent_messages", value);
            FeedbackText = value ? "最近消息将完整显示" : "最近消息将以首行摘要显示";
        }
    }

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (Math.Abs(_volumePercent - normalized) < 0.5) return;
            _volumePercent = normalized;
            OnPropertyChanged();
            _settings.Set("volume", normalized / 100.0);
            FeedbackText = $"音量已调整为 {(int)Math.Round(normalized)}%";
        }
    }

    public string FeedbackText
    {
        get => _feedbackText;
        private set
        {
            if (string.Equals(_feedbackText, value, StringComparison.Ordinal)) return;
            _feedbackText = value;
            OnPropertyChanged();
        }
    }

    private void RefreshMonitorOptions()
    {
        DisplayMonitorOptions.Clear();
        DisplayMonitorOptions.Add(new DisplayMonitorOption(WpfMonitorService.AutoMonitorId, "自动（跟随鼠标所在显示器）"));

        foreach (var monitor in WpfMonitorService.GetMonitors())
            DisplayMonitorOptions.Add(new DisplayMonitorOption(monitor.Id, monitor.DisplayName));

        if (!DisplayMonitorOptions.Any(option => string.Equals(option.Id, _displayMonitor, StringComparison.OrdinalIgnoreCase)))
        {
            _displayMonitor = WpfMonitorService.AutoMonitorId;
            OnPropertyChanged(nameof(DisplayMonitor));
            _settings.Set("display_monitor", _displayMonitor);
            FeedbackText = "未找到之前选择的显示器，已回退到自动";
        }
    }

    private string ReadDisplayPosition()
    {
        var configured = _settings.Get("display_position", WpfHudDisplayPosition.Default);
        var normalized = WpfHudDisplayPosition.Normalize(configured);
        if (_settings.Has("display_position") || !string.Equals(configured, normalized, StringComparison.Ordinal))
            _settings.Set("display_position", normalized);

        return normalized;
    }

    private string ReadHudDensityMode()
    {
        var configured = _settings.Get("hud_density_mode", WpfHudDensityMode.Default);
        var normalized = WpfHudDensityMode.Normalize(configured);
        if (_settings.Has("hud_density_mode") || !string.Equals(configured, normalized, StringComparison.Ordinal))
            _settings.Set("hud_density_mode", normalized);

        return normalized;
    }

    private void ApplySessionTimeout(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutSeconds))
        {
            FeedbackText = "会话超时必须是数字，已保留当前生效值";
            return;
        }

        if (timeoutSeconds is < 30 or > 3600)
        {
            FeedbackText = "会话超时范围为 30–3600 秒，已保留当前生效值";
            return;
        }

        _settings.Set("session_timeout", timeoutSeconds);
        FeedbackText = "会话超时已保存，新的审批/问答等待会使用该值";
    }

    private void ApplyHotkeysIfValid()
    {
        if (!WpfGlobalHotkey.ValidateConfigured(TogglePanelHotkey, ApproveHotkey, DenyHotkey, out var hotkeyMessage))
        {
            FeedbackText = $"快捷键未保存：{hotkeyMessage}";
            return;
        }

        if (HotkeysChangeRequested?.Invoke(TogglePanelHotkey, ApproveHotkey, DenyHotkey) == false)
        {
            FeedbackText = "快捷键未保存：注册失败或已被其他应用占用，已保留原有快捷键";
            return;
        }

        _settings.Set("hotkey_toggle_panel", TogglePanelHotkey);
        _settings.Set("hotkey_approve", ApproveHotkey);
        _settings.Set("hotkey_deny", DenyHotkey);
        FeedbackText = "快捷键已保存并重新注册";
    }

    public void SelectAboutTab() => SelectSection(AboutSectionId);

    public void SelectSection(string sectionId)
    {
        if (string.IsNullOrWhiteSpace(sectionId))
            return;

        foreach (var item in SectionNav.Items.OfType<ListBoxItem>())
        {
            if (!string.Equals(item.Tag as string, sectionId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Same item already selected: SelectionChanged won't fire (e.g. reopen About).
            if (ReferenceEquals(SectionNav.SelectedItem, item))
            {
                if (!string.Equals(sectionId, "hotkeys", StringComparison.OrdinalIgnoreCase))
                    CancelHotkeyRecording();
                ShowSection(sectionId, animate: false);
                return;
            }

            SectionNav.SelectedItem = item;
            return;
        }
    }

    private void RegisterSections()
    {
        _sections["general"] = SectionGeneral;
        _sections["behavior"] = SectionBehavior;
        _sections["appearance"] = SectionAppearance;
        _sections["hotkeys"] = SectionHotkeys;
        _sections["tools"] = SectionTools;
        _sections[AboutSectionId] = SectionAbout;
    }

    private void SectionNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionNav.SelectedItem is not ListBoxItem item || item.Tag is not string sectionId)
            return;

        if (string.Equals(sectionId, _activeSectionId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.Equals(sectionId, "hotkeys", StringComparison.OrdinalIgnoreCase))
            CancelHotkeyRecording();

        ShowSection(sectionId, animate: true);
    }

    private void ShowSection(string sectionId, bool animate)
    {
        if (!_sections.TryGetValue(sectionId, out var next))
            return;

        var previousId = _activeSectionId;
        _sections.TryGetValue(previousId, out var previous);
        _activeSectionId = sectionId;

        // Mid-transition clicks: land on the latest section without stacking animations.
        if (_sectionTransitionInProgress)
        {
            FinishSectionTransition(next);
            return;
        }

        if (!animate || previous is null || ReferenceEquals(previous, next))
        {
            FinishSectionTransition(next);
            return;
        }

        _sectionTransitionInProgress = true;
        var settings = HudAnimationSettings.ForCurrentRenderer();
        var duration = settings.ContentDuration;
        var slide = settings.AllowsContentMotion ? settings.ContentSlideOffset : 0d;

        var fadeOut = new DoubleAnimation(1, 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fadeOut.Completed += (_, _) =>
        {
            // Selection may have moved again while fading out.
            if (!_sections.TryGetValue(_activeSectionId, out var target))
                target = next;

            foreach (var section in _sections.Values)
                section.Visibility = ReferenceEquals(section, target) ? Visibility.Visible : Visibility.Collapsed;

            ContentHostTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ContentHostTranslate.X = slide;
            var fadeIn = new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += (_, _) =>
            {
                _sectionTransitionInProgress = false;
                // Apply any selection that arrived during fade-in.
                if (_sections.TryGetValue(_activeSectionId, out var latest) && !ReferenceEquals(latest, target))
                    FinishSectionTransition(latest);
            };
            ContentHost.BeginAnimation(OpacityProperty, fadeIn);

            if (slide > 0)
            {
                var slideIn = new DoubleAnimation(slide, 0, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                ContentHostTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);
            }
            else
            {
                ContentHostTranslate.X = 0;
            }
        };

        ContentHost.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void FinishSectionTransition(FrameworkElement next)
    {
        ContentHost.BeginAnimation(OpacityProperty, null);
        ContentHostTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        foreach (var section in _sections.Values)
            section.Visibility = ReferenceEquals(section, next) ? Visibility.Visible : Visibility.Collapsed;
        ContentHost.Opacity = 1;
        ContentHostTranslate.X = 0;
        _sectionTransitionInProgress = false;
    }

    private void RecordToggleHotkey_Click(object sender, RoutedEventArgs e) => BeginHotkeyRecording("toggle");

    private void RecordApproveHotkey_Click(object sender, RoutedEventArgs e) => BeginHotkeyRecording("approve");

    private void RecordDenyHotkey_Click(object sender, RoutedEventArgs e) => BeginHotkeyRecording("deny");

    private void BeginHotkeyRecording(string field)
    {
        _recordingHotkeyField = field;
        NotifyHotkeyRecordLabels();
        FeedbackText = "正在录制快捷键，按下组合键；Esc 取消";
        SectionHotkeys.Focusable = true;
        // Click focus stays on the button; force section so PreviewKeyDown receives keys.
        SectionHotkeys.Focus();
        Keyboard.Focus(SectionHotkeys);
    }

    private void CancelHotkeyRecording()
    {
        if (_recordingHotkeyField is null)
            return;

        _recordingHotkeyField = null;
        NotifyHotkeyRecordLabels();
        FeedbackText = "已取消快捷键录制";
    }

    private void NotifyHotkeyRecordLabels()
    {
        OnPropertyChanged(nameof(ToggleHotkeyRecordLabel));
        OnPropertyChanged(nameof(ApproveHotkeyRecordLabel));
        OnPropertyChanged(nameof(DenyHotkeyRecordLabel));
    }

    private void HotkeySection_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingHotkeyField is null)
            return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (key == Key.Escape)
        {
            CancelHotkeyRecording();
            return;
        }

        if (!TryFormatHotkey(Keyboard.Modifiers, key, out var hotkeyText, out var error))
        {
            FeedbackText = $"快捷键无效：{error}";
            return;
        }

        var field = _recordingHotkeyField;
        _recordingHotkeyField = null;
        NotifyHotkeyRecordLabels();

        switch (field)
        {
            case "toggle":
                TogglePanelHotkey = hotkeyText;
                break;
            case "approve":
                ApproveHotkey = hotkeyText;
                break;
            case "deny":
                DenyHotkey = hotkeyText;
                break;
        }
    }

    private static bool TryFormatHotkey(ModifierKeys modifiers, Key key, out string text, out string error)
    {
        text = "";
        error = "";
        if (modifiers == ModifierKeys.None)
        {
            error = "必须包含 Ctrl、Alt、Shift 或 Win 修饰键";
            return false;
        }

        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");

        var keyText = key switch
        {
            >= Key.A and <= Key.Z => key.ToString().ToUpperInvariant(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            >= Key.F1 and <= Key.F24 => key.ToString().ToUpperInvariant(),
            _ => null
        };

        if (keyText is null)
        {
            error = $"不支持的按键 {key}";
            return false;
        }

        parts.Add(keyText);
        text = string.Join("+", parts);
        return true;
    }

    public sealed record DisplayMonitorOption(string Id, string DisplayName);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private async Task LoadRuntimeStatusAsync()
    {
        try
        {
            if (_sourceService is not WpfRuntimeApiClient apiClient)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    RuntimeConnectionStatus = "CodeOrbit 未连接";
                    RuntimeStatusColor = System.Windows.Media.Brushes.Gray;
                });
                return;
            }

            var version = await apiClient.GetVersionAsync(CancellationToken.None).ConfigureAwait(false);
            if (version != null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    RuntimeVersion = $"v{version.Version}";
                    RuntimeProduct = version.Product;
                    CurrentVersion = version.Version;
                    RuntimeConnectionStatus = $"CodeOrbit 已连接 {RuntimeVersion}";
                    RuntimeStatusColor = System.Windows.Media.Brushes.ForestGreen;
                });
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    RuntimeConnectionStatus = "CodeOrbit 未连接";
                    RuntimeStatusColor = System.Windows.Media.Brushes.Gray;
                });
            }
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                RuntimeConnectionStatus = "CodeOrbit 未连接";
                RuntimeStatusColor = System.Windows.Media.Brushes.Gray;
            });
        }
    }

    private async Task LoadSourcesAsync()
    {
        var loadGen = Interlocked.Increment(ref _wslLoadGeneration);
        try
        {
            // Windows sources first — never block UI on WSL (wsl.exe can hang for seconds).
            var sources = await Task.Run(() => _sourceService.GetSources()).ConfigureAwait(false);
            if (loadGen != _wslLoadGeneration)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                Sources.Clear();
                foreach (var dto in sources)
                {
                    Sources.Add(new SourceViewModel(dto)
                    {
                        WslAvailable = false,
                        WslInstalled = false,
                        SelectedDistro = null
                    });
                }
                SourcesLoaded = Sources.Count > 0;
                WslAvailable = false;
            });

            // Distro list only; per-source status is best-effort and timed out separately.
            var listResult = await RunWithTimeoutAsync(
                () =>
                {
                    try
                    {
                        return _sourceService.ListWslDistros();
                    }
                    catch
                    {
                        return new WslDistrosDto([]);
                    }
                },
                WslListTimeout,
                new WslDistrosDto([])).ConfigureAwait(false);

            if (loadGen != _wslLoadGeneration)
                return;

            var distroItems = (listResult.Distros ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(d => new WslDistroItem(d.Name.Trim(), FormatWslDistroLabel(d)))
                .ToList();

            string? selected = null;
            if (distroItems.Count > 0)
            {
                var names = distroItems.Select(d => d.Name).ToList();
                if (!string.IsNullOrWhiteSpace(listResult.DefaultDistro) &&
                    names.Contains(listResult.DefaultDistro.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    selected = names.First(n => string.Equals(n, listResult.DefaultDistro.Trim(), StringComparison.OrdinalIgnoreCase));
                }
                else if (!string.IsNullOrWhiteSpace(_selectedWslDistro) &&
                         names.Contains(_selectedWslDistro, StringComparer.OrdinalIgnoreCase))
                {
                    selected = names.First(n => string.Equals(n, _selectedWslDistro, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    selected = distroItems[0].Name;
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                _suppressWslDistroRefresh = true;
                try
                {
                    _wslDistros.Clear();
                    foreach (var d in distroItems)
                        _wslDistros.Add(d);

                    // ItemsSource rebuild clears ComboBox SelectedValue; force notify even if name unchanged.
                    _selectedWslDistro = selected;
                    OnPropertyChanged(nameof(SelectedWslDistro));
                    if (!string.IsNullOrWhiteSpace(selected))
                        _settings.Set("last_wsl_distro", selected);

                    WslAvailable = distroItems.Count > 0;
                    foreach (var vm in Sources)
                    {
                        vm.WslAvailable = WslAvailable;
                        vm.SelectedDistro = selected;
                        // leave WslInstalled until background status refresh finishes
                    }

                    if (distroItems.Count == 0 &&
                        (!string.IsNullOrWhiteSpace(listResult.Message) || !string.IsNullOrWhiteSpace(listResult.Code)))
                    {
                        FeedbackText = FormatSourceError("WSL 发行版列表不可用", listResult.Message, listResult.Code);
                    }
                }
                finally
                {
                    _suppressWslDistroRefresh = false;
                }
            });

            if (distroItems.Count > 0 && !string.IsNullOrWhiteSpace(selected))
                await RefreshWslStatusesAsync(loadGen).ConfigureAwait(false);
        }
        catch
        {
            if (loadGen != _wslLoadGeneration)
                return;
            await Dispatcher.InvokeAsync(() =>
            {
                SourcesLoaded = false;
                WslAvailable = false;
                FeedbackText = "工具列表加载失败：Runtime 未连接";
            });
        }
    }

    private async Task RefreshWslStatusesAsync(int? expectedGeneration = null)
    {
        string? distro = null;
        List<string> ids = [];
        await Dispatcher.InvokeAsync(() =>
        {
            if (!WslAvailable || string.IsNullOrWhiteSpace(SelectedWslDistro))
                return;
            distro = SelectedWslDistro;
            ids = Sources.Select(s => s.Id).ToList();
        });

        if (string.IsNullOrWhiteSpace(distro) || ids.Count == 0)
            return;
        if (expectedGeneration is int gen && gen != _wslLoadGeneration)
            return;

        var statuses = await RunWithTimeoutAsync(
            () =>
            {
                var map = new Dictionary<string, WslStatusView>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in ids)
                {
                    try
                    {
                        var status = _sourceService.GetWslSourceStatus(id, distro);
                        var probeFailed = status.ProbeOk == false;
                        map[id] = new WslStatusView(
                            probeFailed ? null : status.Installed,
                            probeFailed,
                            status.Error);
                    }
                    catch
                    {
                        map[id] = new WslStatusView(null, ProbeFailed: true, Error: "WSL 状态查询失败");
                    }
                }
                return map;
            },
            WslStatusTimeout,
            new Dictionary<string, WslStatusView>(StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);

        if (expectedGeneration is int gen2 && gen2 != _wslLoadGeneration)
            return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (!string.Equals(SelectedWslDistro, distro, StringComparison.OrdinalIgnoreCase))
                return;

            string? probeError = null;
            foreach (var vm in Sources)
            {
                vm.SelectedDistro = distro;
                vm.WslAvailable = true;
                if (!statuses.TryGetValue(vm.Id, out var status))
                    continue;

                if (status.ProbeFailed)
                {
                    // probeOk=false: installed is untrusted — do not force "未安装".
                    probeError ??= status.Error;
                    continue;
                }

                if (status.Installed is bool installed)
                    vm.WslInstalled = installed;
            }

            if (!string.IsNullOrWhiteSpace(probeError))
                FeedbackText = FormatSourceError("WSL 探测失败", probeError, "wsl_unavailable");
        });
    }

    private async void SourceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not SourceViewModel vm)
            return;

        vm.IsOperating = true;
        var displayName = vm.DisplayName;
        var wasInstalled = vm.Installed;
        var sourceId = vm.Id;

        try
        {
            var result = await Task.Run(() =>
                wasInstalled
                    ? _sourceService.Uninstall(sourceId)
                    : _sourceService.Install(sourceId)).ConfigureAwait(true);

            FeedbackText = result.Success
                ? $"{displayName} 已{(wasInstalled ? "断开" : "连接")}"
                : BuildOperationFailureText(displayName, wasInstalled, result.Message, result.Code);

            await LoadSourcesAsync().ConfigureAwait(true);
        }
        catch
        {
            FeedbackText = $"{displayName} 操作失败：Runtime 未连接";
        }
        finally
        {
            vm.IsOperating = false;
        }
    }

    private async void WslSourceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not SourceViewModel vm)
            return;
        if (string.IsNullOrWhiteSpace(SelectedWslDistro))
        {
            FeedbackText = "未选择 WSL 发行版";
            return;
        }

        vm.IsWslOperating = true;
        var displayName = vm.DisplayName;
        var distro = SelectedWslDistro;
        var wasInstalled = vm.WslInstalled;
        var sourceId = vm.Id;

        try
        {
            var result = await RunWithTimeoutAsync(
                () => wasInstalled
                    ? _sourceService.UninstallWsl(sourceId, distro)
                    : _sourceService.InstallWsl(sourceId, distro),
                WslOperationTimeout,
                new SourceOperationResultDto(sourceId, Success: false, Installed: wasInstalled, Message: "WSL 操作超时", Distro: distro, Code: "operation_failed")).ConfigureAwait(true);

            FeedbackText = result.Success
                ? $"{displayName} 已{(wasInstalled ? "在 WSL 断开" : $"在 WSL({result.Distro ?? distro}) 连接")}"
                : BuildWslOperationFailureText(displayName, wasInstalled, result.Message, result.Code);

            if (result.Success)
            {
                vm.WslInstalled = result.Installed;
            }
            else if (!string.Equals(result.Message, "WSL 操作超时", StringComparison.Ordinal))
            {
                var status = await RunWithTimeoutAsync(
                    () => _sourceService.GetWslSourceStatus(sourceId, distro),
                    TimeSpan.FromSeconds(3),
                    new SourceStatusDto(sourceId, Supported: true, Installed: wasInstalled, DisplayName: displayName, Distro: distro)).ConfigureAwait(true);
                if (status.ProbeOk != false)
                    vm.WslInstalled = status.Installed;
            }
        }
        catch
        {
            FeedbackText = $"{displayName} WSL 操作失败：Runtime 未连接";
        }
        finally
        {
            vm.IsWslOperating = false;
        }
    }

    private static async Task<T> RunWithTimeoutAsync<T>(Func<T> work, TimeSpan timeout, T fallback)
    {
        try
        {
            return await Task.Run(work).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string BuildOperationFailureText(string displayName, bool wasInstalled, string? message, string? code = null)
    {
        var operationText = wasInstalled ? "断开" : "连接";
        if (!string.IsNullOrWhiteSpace(message) && message.Contains("Runtime", StringComparison.OrdinalIgnoreCase))
            return $"{displayName} {operationText}失败：Runtime 未连接";
        return FormatSourceError($"{displayName} {operationText}失败", message, code);
    }

    private static string BuildWslOperationFailureText(string displayName, bool wasInstalled, string? message, string? code = null)
    {
        var operationText = wasInstalled ? "WSL 断开" : "WSL 连接";
        return FormatSourceError($"{displayName} {operationText}失败", message, code);
    }

    private static string FormatSourceError(string prefix, string? message, string? code)
    {
        var mapped = MapSourceErrorCode(code);
        if (!string.IsNullOrWhiteSpace(mapped) && !string.IsNullOrWhiteSpace(message) &&
            !string.Equals(mapped, message, StringComparison.OrdinalIgnoreCase) &&
            !message.Contains(mapped, StringComparison.OrdinalIgnoreCase))
        {
            return $"{prefix}：{mapped}（{message}）";
        }

        if (!string.IsNullOrWhiteSpace(mapped))
            return $"{prefix}：{mapped}";
        if (!string.IsNullOrWhiteSpace(message))
            return $"{prefix}：{message}";
        return prefix;
    }

    private static string? MapSourceErrorCode(string? code) =>
        code?.Trim().ToLowerInvariant() switch
        {
            "wsl_unavailable" => "WSL 不可用或探测失败",
            "missing_bridge" => "缺少 bridge 可执行文件",
            "invalid_distro" => "发行版不可用（系统/Docker 等）",
            "hook_write_failed" => "写入 hook 配置失败",
            "unsupported_source" => "不支持的工具源",
            "operation_failed" => "操作失败",
            _ => null
        };

    private static string FormatWslDistroLabel(WslDistroDto distro)
    {
        var name = distro.Name.Trim();
        var state = string.IsNullOrWhiteSpace(distro.State) ? null : distro.State.Trim();
        var version = distro.Version is uint v and > 0 ? $"WSL{v}" : null;
        var parts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(state))
            parts.Add(state);
        if (!string.IsNullOrWhiteSpace(version))
            parts.Add(version);
        if (distro.IsDefault)
            parts.Add("默认");
        return string.Join(" · ", parts);
    }

    public sealed record WslDistroItem(string Name, string Label);

    private sealed record WslStatusView(bool? Installed, bool ProbeFailed, string? Error);

    private void OpenGitHubReleases_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = GitHubReleasesUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            FeedbackText = "无法打开浏览器，请手动访问 GitHub Releases";
        }
    }
}
