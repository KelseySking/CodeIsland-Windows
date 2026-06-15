using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CodeIsland.WpfApp.Services;
using CodeIsland.WpfApp.ViewModels;
using CodeIsland.WpfApp.Views;

namespace CodeIsland.WpfApp;

public partial class App : System.Windows.Application
{
    private SettingsManager? _settings;
    private WpfRuntimeProcessManager? _runtimeManager;
    private IWpfRuntimeClient? _runtimeClient;
    private WpfAppState? _appState;
    private IWpfSourceService? _sourceService;
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
        _runtimeManager = new WpfRuntimeProcessManager(_settings, logger);
        var runtimeClient = new WpfRuntimeApiClient(_runtimeManager.ApiBaseUrl, _runtimeManager.ApiToken, logger);
        _runtimeClient = runtimeClient;
        _sourceService = runtimeClient;
        _webhookNotifier = new WpfWebhookNotifier(_settings);
        _appState = new WpfAppState(_settings, runtimeClient, _webhookNotifier);
        _appState.PlaySoundRequested += OnPlaySoundRequested;
        _soundManager = new WpfSoundManager
        {
            Enabled = _settings.Get("sound_enabled", true),
            Volume = (float)_settings.Get("volume", 0.7)
        };
        _settings.SettingChanged += OnRuntimeSettingChanged;
        _hudWindow = new HudWindow(_appState, _settings);
        _hudWindow.ShowNoActivate();
        _ = StartRuntimeAsync(logger);

        _tray = new WpfTrayService(_hudWindow, ShowSettings, ShowAbout, Shutdown);
        _hotkey = new WpfGlobalHotkey();
        RegisterHotkeys();
    }

    private async Task StartRuntimeAsync(EventLogger logger)
    {
        if (_runtimeManager != null)
        {
            try
            {
                logger.Write("WpfRuntime", "ensure-start-begin", new Dictionary<string, string?>
                {
                    ["baseUrl"] = _runtimeManager.ApiBaseUrl,
                    ["mode"] = _settings?.Get("runtime_launch_mode", WpfRuntimeProcessManager.ManagedMode)
                });
                await _runtimeManager.EnsureStartedAsync();
                logger.Write("WpfRuntime", "ensure-start-complete", new Dictionary<string, string?>
                {
                    ["ownedRuntime"] = _runtimeManager.OwnsRuntime.ToString()
                });
            }
            catch (Exception ex)
            {
                logger.Write("WpfRuntimeProcessManager", "start-failed", new Dictionary<string, string?>
                {
                    ["message"] = ex.Message,
                    ["exception"] = ex.GetType().Name
                });
            }
        }

        if (_runtimeClient == null)
            return;

        try
        {
            await _runtimeClient.StartAsync();
        }
        catch (Exception ex)
        {
            logger.Write("WpfRuntimeApiClient", "start-failed", new Dictionary<string, string?>
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (_settings != null)
            _settings.SettingChanged -= OnRuntimeSettingChanged;
        if (_appState != null)
            _appState.PlaySoundRequested -= OnPlaySoundRequested;
        _hotkey?.Dispose();
        _tray?.Dispose();
        _appState?.Dispose();
        _runtimeClient?.Dispose();
        _runtimeManager?.Dispose();
        _soundManager?.Dispose();
        _webhookNotifier?.Dispose();
        base.OnExit(e);
    }
}
