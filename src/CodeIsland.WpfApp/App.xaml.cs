using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CodeIsland.Core.Services;
using CodeIsland.Hub;
using CodeIsland.WpfApp.Services;
using CodeIsland.WpfApp.ViewModels;
using CodeIsland.WpfApp.Views;

namespace CodeIsland.WpfApp;

public partial class App : System.Windows.Application
{
    private SettingsManager? _settings;
    private WpfAppState? _appState;
    private WpfHubStateAdapter? _hubStateAdapter;
    private ICodeIslandSourceService? _sourceService;
    private CodeIslandApiHost? _apiHost;
    private WpfHookServer? _hookServer;
    private WpfTrayService? _tray;
    private WpfGlobalHotkey? _hotkey;
    private WpfSoundManager? _soundManager;
    private WpfWebhookNotifier? _webhookNotifier;
    private HudWindow? _hudWindow;
    private string? _lastSoundName;
    private DateTime _lastSoundAt;

    protected override void OnStartup(StartupEventArgs e)
    {
        ConfigureRendering();
        base.OnStartup(e);

        _settings = new SettingsManager();
        var logger = new EventLogger();
        _sourceService = new ConfigInstallerSourceService();
        _ = _sourceService.RepairAll();
        _webhookNotifier = new WpfWebhookNotifier(_settings);
        _appState = new WpfAppState(_settings, logger, _webhookNotifier);
        _appState.PlaySoundRequested += OnPlaySoundRequested;
        _appState.PropertyChanged += OnAppStatePropertyChanged;
        _hubStateAdapter = new WpfHubStateAdapter(_appState);
        _soundManager = new WpfSoundManager
        {
            Enabled = _settings.Get("sound_enabled", true),
            Volume = (float)_settings.Get("volume", 0.7)
        };
        _settings.SettingChanged += OnRuntimeSettingChanged;
        _hudWindow = new HudWindow(_appState, _settings);
        _hudWindow.ShowNoActivate();

        _hookServer = new WpfHookServer(_appState, logger);
        _ = _hookServer.StartAsync();

        var apiToken = LocalApiTokenStore.EnsureToken(_settings);
        var apiPort = Math.Clamp(_settings.Get("api_port", 32145), 1024, 65535);
        _apiHost = new CodeIslandApiHost(CodeIslandApiOptions.Localhost(apiToken, apiPort), _hubStateAdapter, _sourceService, logger);
        _ = StartApiHostAsync(logger);

        _tray = new WpfTrayService(_hudWindow, ShowSettings, ShowAbout, Shutdown);
        _hotkey = new WpfGlobalHotkey();
        RegisterHotkeys();
    }

    private async Task StartApiHostAsync(EventLogger logger)
    {
        if (_apiHost == null)
            return;

        try
        {
            await _apiHost.StartAsync();
        }
        catch (Exception ex)
        {
            logger.Write("CodeIslandApiHost", "start-failed", new Dictionary<string, string?>
            {
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
        }
    }

    private static void ConfigureRendering()
    {
        RenderOptions.ProcessRenderMode = RenderMode.Default;
    }

    private void RegisterHotkeys()
    {
        if (_settings == null || _appState == null || _hudWindow == null || _hotkey == null)
            return;

        var toggle = _settings.Get("hotkey_toggle_panel", "Ctrl+Alt+I");
        var approve = _settings.Get("hotkey_approve", "Ctrl+Alt+Y");
        var deny = _settings.Get("hotkey_deny", "Ctrl+Alt+N");
        TryRegisterHotkeys(toggle, approve, deny);
    }

    private bool TryRegisterHotkeys(string toggle, string approve, string deny)
    {
        if (_appState == null || _hudWindow == null || _hotkey == null)
            return false;

        var success = _hotkey.RegisterConfigured(
            toggle,
            approve,
            deny,
            () => _hudWindow.ToggleExpanded(),
            () => _appState.Approve(false),
            () => _appState.Deny(),
            out var message);
        System.Diagnostics.Debug.WriteLine($"[WpfGlobalHotkey] {message}");
        return success;
    }

    private void ShowSettings()
    {
        ShowSettingsWindow(selectAboutTab: false);
    }

    private void ShowAbout()
    {
        ShowSettingsWindow(selectAboutTab: true);
    }

    private void ShowSettingsWindow(bool selectAboutTab)
    {
        if (_settings == null)
            return;

        var window = new SettingsWindow(_settings, _sourceService);
        if (selectAboutTab)
            window.SelectAboutTab();
        window.HotkeysChangeRequested += TryRegisterHotkeys;
        window.Show();
        window.Activate();
    }

    private void OnPlaySoundRequested(string soundName)
    {
        if (_soundManager == null || !ShouldPlaySound(soundName))
            return;

        _soundManager.Play(soundName);
    }

    private bool ShouldPlaySound(string soundName)
    {
        if (_settings?.Get("smart_suppression", true) != true)
            return true;

        var now = DateTime.UtcNow;
        if (string.Equals(_lastSoundName, soundName, StringComparison.Ordinal) && now - _lastSoundAt < TimeSpan.FromMilliseconds(1500))
            return false;

        _lastSoundName = soundName;
        _lastSoundAt = now;
        return true;
    }

    private void OnRuntimeSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (_settings == null || _soundManager == null)
            return;

        switch (e.Key)
        {
            case "sound_enabled":
                _soundManager.Enabled = _settings.Get("sound_enabled", true);
                break;
            case "volume":
                _soundManager.Volume = (float)_settings.Get("volume", 0.7);
                break;
        }
    }

    private void OnAppStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_apiHost == null || _hubStateAdapter == null)
            return;

        if (e.PropertyName is nameof(WpfAppState.PendingActionRevision)
            or nameof(WpfAppState.HasPendingAction)
            or nameof(WpfAppState.HasPendingPermission)
            or nameof(WpfAppState.HasPendingQuestion))
        {
            _ = _apiHost.Realtime.PublishAsync("pending.updated", _hubStateAdapter.GetPendingActions());
            return;
        }

        if (e.PropertyName is nameof(WpfAppState.Sessions)
            or nameof(WpfAppState.SessionCountText)
            or nameof(WpfAppState.ActiveStatus)
            or nameof(WpfAppState.ActiveStatusText)
            or nameof(WpfAppState.DetailAssistantReply)
            or nameof(WpfAppState.DetailUserPrompt))
        {
            _ = _apiHost.Realtime.PublishAsync("session.updated", _hubStateAdapter.GetSessions());
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_settings != null)
            _settings.SettingChanged -= OnRuntimeSettingChanged;
        if (_appState != null)
        {
            _appState.PlaySoundRequested -= OnPlaySoundRequested;
            _appState.PropertyChanged -= OnAppStatePropertyChanged;
        }
        _hotkey?.Dispose();
        _tray?.Dispose();
        _hookServer?.Dispose();
        _apiHost?.Dispose();
        _appState?.Dispose();
        _soundManager?.Dispose();
        _webhookNotifier?.Dispose();
        base.OnExit(e);
    }
}
