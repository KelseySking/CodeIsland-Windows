using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using CodeIsland.WpfApp.Services;

namespace CodeIsland.WpfApp.Views;

public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsManager _settings;
    private readonly IWpfSourceService _sourceService;
    private bool _autoApproveSafeTools;
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

    public SettingsWindow(SettingsManager settings, IWpfSourceService? sourceService = null)
    {
        InitializeComponent();
        _settings = settings;
        _sourceService = sourceService ?? new UnavailableWpfSourceService();
        _autoApproveSafeTools = _settings.Get("auto_approve_safe_tools", false);
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
        DataContext = this;
        RefreshMonitorOptions();
        RefreshHookButtons();
    }

    public event Func<string, string, string, bool>? HotkeysChangeRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DisplayMonitorOption> DisplayMonitorOptions { get; } = new();

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

    public void SelectAboutTab()
    {
        SettingsTabs.SelectedIndex = Math.Max(0, SettingsTabs.Items.Count - 1);
    }

    private void HookToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string source)
            return;

        var displayName = GetSourceDisplayName(source);
        if (!TryGetSourceInstalled(source, out var installed))
        {
            FeedbackText = $"{displayName} 状态读取失败：Runtime 未连接或启动中";
            RefreshHookButtons();
            return;
        }

        try
        {
            var result = installed ? _sourceService.Uninstall(source) : _sourceService.Install(source);
            FeedbackText = result.Success
                ? $"{displayName} 已{(installed ? "断开" : "连接")}"
                : BuildHookFailureText(displayName, installed, result.Message);
        }
        catch
        {
            FeedbackText = $"{displayName} {(installed ? "断开" : "连接")}失败：Runtime 未连接或启动中";
        }

        RefreshHookButtons();
    }

    private bool TryGetSourceInstalled(string source, out bool installed)
    {
        try
        {
            var status = _sourceService.GetSourceStatus(source);
            installed = status.Installed;
            return status.Supported;
        }
        catch
        {
            installed = false;
            return false;
        }
    }

    private static string BuildHookFailureText(string displayName, bool wasInstalled, string? message)
    {
        var operationText = wasInstalled ? "断开" : "连接";
        if (!string.IsNullOrWhiteSpace(message) &&
            message.Contains("Runtime", StringComparison.OrdinalIgnoreCase))
        {
            return $"{displayName} {operationText}失败：Runtime 未连接或启动中";
        }

        return $"{displayName} {operationText}失败";
    }

    private void RefreshHookButtons()
    {
        UpdateHookButton(ClaudeHookButton, "claude");
        UpdateHookButton(CodexHookButton, "codex");
    }

    private static string GetSourceDisplayName(string source) => source.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "Claude Code" : "Codex";

    private void UpdateHookButton(System.Windows.Controls.Button button, string source)
    {
        if (!TryGetSourceInstalled(source, out var installed))
        {
            button.Content = "连接";
            button.Background = System.Windows.Media.Brushes.DarkGray;
            button.Foreground = System.Windows.Media.Brushes.White;
            return;
        }

        button.Content = installed ? "断开" : "连接";
        button.Background = installed
            ? System.Windows.Media.Brushes.IndianRed
            : System.Windows.Media.Brushes.ForestGreen;
        button.Foreground = System.Windows.Media.Brushes.White;
    }

    public sealed record DisplayMonitorOption(string Id, string DisplayName);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
