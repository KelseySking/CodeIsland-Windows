using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeIsland.Contracts;
using CodeIsland.WpfApp.Services;
using CodeIsland.WpfApp.ViewModels;

namespace CodeIsland.WpfApp.Views;

public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private const string UpdateManifestUrl = "https://raw.githubusercontent.com/KelseySking/CodeOrbit/main/update-manifest.json";
    private const string GitHubReleasesUrl = "https://github.com/KelseySking/CodeOrbit/releases";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

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

    // Runtime 状态栏
    private string _runtimeConnectionStatus = "CodeOrbit 连接中...";
    private string _runtimeVersion = "";
    private System.Windows.Media.Brush _runtimeStatusColor = System.Windows.Media.Brushes.Gray;

    // 工具列表
    private bool _sourcesLoaded;

    // CodeOrbit 版本检测
    private string _runtimeProduct = "";
    private string _currentVersion = "";
    private string _latestVersion = "";
    private string _updateCheckStatus = "";

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

        // 加载 Runtime 状态
        _ = LoadRuntimeStatusAsync();

        // 加载工具列表
        _ = LoadSourcesAsync();
    }

    public event Func<string, string, string, bool>? HotkeysChangeRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DisplayMonitorOption> DisplayMonitorOptions { get; } = new();

    // 工具列表
    public ObservableCollection<SourceViewModel> Sources { get; } = new();

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

    public string LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (string.Equals(_latestVersion, value, StringComparison.Ordinal)) return;
            _latestVersion = value;
            OnPropertyChanged();
        }
    }

    public string UpdateCheckStatus
    {
        get => _updateCheckStatus;
        set
        {
            if (string.Equals(_updateCheckStatus, value, StringComparison.Ordinal)) return;
            _updateCheckStatus = value;
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
        try
        {
            var sources = _sourceService.GetSources();
            await Dispatcher.InvokeAsync(() =>
            {
                Sources.Clear();
                foreach (var dto in sources)
                {
                    Sources.Add(new SourceViewModel(dto));
                }
                SourcesLoaded = Sources.Count > 0;
            });
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SourcesLoaded = false;
                FeedbackText = "工具列表加载失败：Runtime 未连接";
            });
        }
    }

    private async void SourceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not SourceViewModel vm)
            return;

        vm.IsOperating = true;
        var displayName = vm.DisplayName;

        try
        {
            var result = vm.Installed
                ? _sourceService.Uninstall(vm.Id)
                : _sourceService.Install(vm.Id);

            FeedbackText = result.Success
                ? $"{displayName} 已{(vm.Installed ? "断开" : "连接")}"
                : BuildOperationFailureText(displayName, vm.Installed, result.Message);

            // 刷新列表
            await LoadSourcesAsync();
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

    private static string BuildOperationFailureText(string displayName, bool wasInstalled, string? message)
    {
        var operationText = wasInstalled ? "断开" : "连接";
        if (!string.IsNullOrWhiteSpace(message) && message.Contains("Runtime", StringComparison.OrdinalIgnoreCase))
            return $"{displayName} {operationText}失败：Runtime 未连接";
        return $"{displayName} {operationText}失败";
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsTabs.SelectedIndex == GetCodeOrbitTabIndex())
        {
            _ = CheckForUpdatesAsync();
        }
    }

    private int GetCodeOrbitTabIndex()
    {
        return SettingsTabs.Items.Count - 1;
    }

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

    private async Task CheckForUpdatesAsync()
    {
        if (string.IsNullOrEmpty(CurrentVersion))
            return;

        UpdateCheckStatus = "检测更新中...";

        try
        {
            var manifest = await FetchUpdateManifestAsync().ConfigureAwait(false);
            if (manifest == null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateCheckStatus = "检测失败，请稍后重试";
                });
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                LatestVersion = manifest.RuntimeVersion;
                var comparison = CompareVersions(CurrentVersion, manifest.RuntimeVersion);
                UpdateCheckStatus = comparison switch
                {
                    < 0 => $"有新版本可用：v{manifest.RuntimeVersion}",
                    0 => "已是最新版本",
                    > 0 => "当前版本较新（开发版本）"
                };
            });
        }
        catch (TaskCanceledException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateCheckStatus = "检测超时，请稍后重试";
            });
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateCheckStatus = "检测失败，请稍后重试";
            });
        }
    }

    private static async Task<UpdateManifest?> FetchUpdateManifestAsync()
    {
        try
        {
            return await HttpClient.GetFromJsonAsync<UpdateManifest>(UpdateManifestUrl).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static int CompareVersions(string current, string latest)
    {
        if (!Version.TryParse(current, out var v1) || !Version.TryParse(latest, out var v2))
            return 0;
        return v1.CompareTo(v2);
    }

    private sealed record UpdateManifest(string RuntimeVersion, string ContractVersion, string DownloadUrl, string Sha256);
}
