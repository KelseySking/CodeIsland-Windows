using System.Drawing;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Forms;
using CodeIsland.WpfApp.Views;

namespace CodeIsland.WpfApp.Services;

public sealed class WpfTrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly HudWindow _hudWindow;
    private readonly Func<Task<(bool Connected, string Text)>> _getRuntimeStatus;
    private readonly Action _showSettings;
    private readonly Action _showAbout;
    private readonly Action _exit;
    private TrayMenuPopup? _menu;
    private bool _openingMenu;

    public WpfTrayService(
        HudWindow hudWindow,
        Func<Task<(bool Connected, string Text)>> getRuntimeStatus,
        Action showSettings,
        Action showAbout,
        Action exit)
    {
        _hudWindow = hudWindow;
        _getRuntimeStatus = getRuntimeStatus;
        _showSettings = showSettings;
        _showAbout = showAbout;
        _exit = exit;

        _notifyIcon = new NotifyIcon
        {
            Text = "CodeIsland",
            Visible = true,
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? SystemIcons.Application
        };
        // Keep NotifyIcon click/double-click; replace ContextMenuStrip with WPF popup.
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
        _notifyIcon.DoubleClick += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(_hudWindow.ToggleExpanded);
    }

    private void OnNotifyIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(_hudWindow.ShowNoActivate);
            return;
        }

        if (e.Button != MouseButtons.Right)
            return;

        _ = OpenMenuAsync();
    }

    private async Task OpenMenuAsync()
    {
        if (_openingMenu)
            return;
        _openingMenu = true;
        try
        {
            var status = await _getRuntimeStatus().ConfigureAwait(true);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CloseMenu();
                _menu = new TrayMenuPopup(
                    isHudVisible: _hudWindow.IsHudVisible,
                    isHudExpanded: _hudWindow.IsHudExpanded,
                    runtimeConnected: status.Connected,
                    runtimeStatusText: status.Text,
                    showOrHideHud: () =>
                    {
                        if (_hudWindow.IsHudVisible)
                            _hudWindow.HideHud();
                        else
                            _hudWindow.ShowNoActivate();
                    },
                    toggleExpanded: _hudWindow.ToggleExpanded,
                    showSettings: _showSettings,
                    showAbout: _showAbout,
                    exit: _exit,
                    onClosedByUser: () => _menu = null);
                _menu.ShowNearCursor();
            });
        }
        finally
        {
            _openingMenu = false;
        }
    }

    private void CloseMenu()
    {
        if (_menu is null)
            return;
        try
        {
            if (_menu.IsVisible)
                _menu.Close();
        }
        catch
        {
            // ignore
        }
        _menu = null;
    }

    public void Dispose()
    {
        CloseMenu();
        _notifyIcon.Visible = false;
        _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
        _notifyIcon.Dispose();
    }
}

public static class WpfTrayRuntimeStatus
{
    public static async Task<(bool Connected, string Text)> ProbeAsync(string apiBaseUrl, string apiToken, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/api/"),
                Timeout = TimeSpan.FromMilliseconds(700)
            };
            if (!string.IsNullOrWhiteSpace(apiToken))
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiToken}");

            var health = await http.GetFromJsonAsync<HealthProbe>("health", ct).ConfigureAwait(false);
            if (string.Equals(health?.Status, "ok", StringComparison.OrdinalIgnoreCase))
                return (true, "● 已连接");
        }
        catch
        {
            // fall through
        }

        return (false, "● 未连接");
    }

    private sealed record HealthProbe(string? Status);
}
