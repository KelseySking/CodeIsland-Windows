using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CodeIsland.WpfApp.Services;
using CodeIsland.WpfApp.ViewModels;
using CodeIsland.WpfApp.Views;

namespace CodeIsland.WpfApp;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\CodeIsland-Windows-SingleInstance";

    private Mutex? _singleInstanceMutex;
    private SettingsManager? _settings;
    private WpfPetCatalogService? _petCatalog;
    private EventLogger? _logger;
    private WpfRuntimeProcessManager? _runtimeManager;
    private IWpfRuntimeClient? _runtimeClient;
    private WpfAppState? _appState;
    private IWpfSourceService? _sourceService;
    private WpfTrayService? _tray;
    private WpfGlobalHotkey? _hotkey;
    private WpfSoundManager? _soundManager;
    private WpfWebhookNotifier? _webhookNotifier;
    private HudWindow? _hudWindow;
    private SettingsWindow? _settingsWindow;
    private string? _lastSoundName;
    private DateTime _lastSoundAt;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryAcquireSingleInstance())
        {
            System.Windows.MessageBox.Show(
                "CodeIsland 已在运行。\n请从托盘图标继续使用，或先退出已运行的实例。",
                "CodeIsland",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ConfigureRendering();
        base.OnStartup(e);

        _settings = new SettingsManager();
        _petCatalog = new WpfPetCatalogService(_settings);
        _logger = new EventLogger();
        _runtimeManager = new WpfRuntimeProcessManager(_settings, _logger);
        var runtimeClient = new WpfRuntimeApiClient(_runtimeManager.ApiBaseUrl, _runtimeManager.ApiToken, _logger);
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
        _hudWindow = new HudWindow(_appState, _settings, _petCatalog);
        _hudWindow.ShowNoActivate();
        _ = StartRuntimeAsync(_logger);

        // 闭包读取当前 manager，避免托盘探测使用陈旧 baseUrl/token
        _tray = new WpfTrayService(
            _hudWindow,
            () =>
            {
                var manager = _runtimeManager;
                if (manager == null)
                    return Task.FromResult((false, "● 未连接"));
                return WpfTrayRuntimeStatus.ProbeAsync(manager.ApiBaseUrl, manager.ApiToken);
            },
            ShowSettings,
            ShowAbout,
            Shutdown);
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

    /// <summary>
    /// 应用内重连 CodeOrbit：先接线新 client 并退订旧 client → Dispose 旧连接 →
    /// 按需停自有 host → EnsureStarted → Start 新 client。
    /// </summary>
    public async Task<(bool Success, string Message)> ReconnectCodeOrbitAsync()
    {
        if (_settings == null || _runtimeManager == null || _appState == null)
            return (false, "应用尚未就绪");

        if (!await _reconnectLock.WaitAsync(0).ConfigureAwait(false))
            return (false, "正在重连，请稍候");

        var logger = _logger ?? new EventLogger();
        try
        {
            logger.Write("WpfRuntime", "reconnect-begin", new Dictionary<string, string?>
            {
                ["baseUrl"] = _runtimeManager.ApiBaseUrl,
                ["mode"] = _settings.Get("runtime_launch_mode", WpfRuntimeProcessManager.ManagedMode)
            });

            var oldClient = _runtimeClient;

            // 先按最新 settings 创建 client 并完成重绑，避免停 host 时旧 WS 仍向 AppState 推事件。
            var newClient = new WpfRuntimeApiClient(_runtimeManager.ApiBaseUrl, _runtimeManager.ApiToken, logger);
            _runtimeClient = newClient;
            _sourceService = newClient;
            _appState.ReplaceClient(newClient);
            _settingsWindow?.ReplaceSourceService(newClient);

            if (oldClient != null && !ReferenceEquals(oldClient, newClient))
            {
                try
                {
                    oldClient.Dispose();
                }
                catch (Exception ex)
                {
                    logger.Write("WpfRuntimeApiClient", "dispose-failed", new Dictionary<string, string?>
                    {
                        ["message"] = ex.Message,
                        ["exception"] = ex.GetType().Name
                    });
                }
            }

            try
            {
                _runtimeManager.StopOwnedIfNeeded();
            }
            catch (Exception ex)
            {
                logger.Write("WpfRuntimeProcessManager", "stop-owned-failed", new Dictionary<string, string?>
                {
                    ["message"] = ex.Message,
                    ["exception"] = ex.GetType().Name
                });
            }

            // 短暂等待旧 host 释放端口
            await Task.Delay(300).ConfigureAwait(false);

            try
            {
                await _runtimeManager.EnsureStartedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Write("WpfRuntimeProcessManager", "reconnect-ensure-failed", new Dictionary<string, string?>
                {
                    ["message"] = ex.Message,
                    ["exception"] = ex.GetType().Name
                });
            }

            try
            {
                await newClient.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Write("WpfRuntimeApiClient", "reconnect-start-failed", new Dictionary<string, string?>
                {
                    ["message"] = ex.Message,
                    ["exception"] = ex.GetType().Name
                });
                return (false, $"已切换连接参数，但 CodeOrbit 未就绪：{ex.Message}");
            }

            var version = await newClient.GetVersionAsync().ConfigureAwait(false);
            if (version == null)
            {
                logger.Write("WpfRuntime", "reconnect-unhealthy", new Dictionary<string, string?>
                {
                    ["baseUrl"] = _runtimeManager.ApiBaseUrl
                });
                return (false, "已应用设置，但当前无法连接 CodeOrbit。external 模式请先手动启动实例，或检查 host/port/token。");
            }

            logger.Write("WpfRuntime", "reconnect-complete", new Dictionary<string, string?>
            {
                ["baseUrl"] = _runtimeManager.ApiBaseUrl,
                ["version"] = version.Version
            });
            return (true, $"已重连 CodeOrbit v{version.Version}");
        }
        catch (Exception ex)
        {
            logger.Write("WpfRuntime", "reconnect-exception", new Dictionary<string, string?>
            {
                ["message"] = ex.Message,
                ["exception"] = ex.GetType().Name
            });
            return (false, $"重连失败：{ex.Message}");
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private bool TryAcquireSingleInstance()
    {
        // ponytail: 命名互斥体足够；跨会话/提权再考虑 Global\\
        var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        _singleInstanceMutex = mutex;
        return true;
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

        if (_settingsWindow != null)
        {
            if (selectAboutTab)
                _settingsWindow.SelectAboutTab();
            _settingsWindow.BringToFront();
            return;
        }

        var window = new SettingsWindow(_settings, _sourceService, _petCatalog);
        _settingsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
                _settingsWindow = null;
        };
        if (selectAboutTab)
            window.SelectAboutTab();
        window.HotkeysChangeRequested += TryRegisterHotkeys;
        window.CodeOrbitReconnectRequested += ReconnectCodeOrbitAsync;
        // 不 Owner 到 Topmost HUD，否则 z-order/激活会被 HUD 拖累
        window.Show();
        window.BringToFront();
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
        _reconnectLock.Dispose();
        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 未拥有锁时忽略
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
