using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using CodeIsland.WpfApp.Views;

namespace CodeIsland.WpfApp.Services;

public sealed class WpfTrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public WpfTrayService(HudWindow hudWindow, Action showSettings, Action showAbout, Action exit)
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "CodeIsland",
            Visible = true,
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? SystemIcons.Application,
            ContextMenuStrip = BuildMenu(hudWindow, showSettings, showAbout, exit)
        };
        _notifyIcon.Click += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(hudWindow.ShowNoActivate);
        _notifyIcon.DoubleClick += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(hudWindow.ToggleExpanded);
    }

    private static ContextMenuStrip BuildMenu(HudWindow hudWindow, Action showSettings, Action showAbout, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示 HUD", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(hudWindow.ShowNoActivate));
        menu.Items.Add("隐藏 HUD", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(hudWindow.HideHud));
        menu.Items.Add("展开/收起 HUD", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(hudWindow.ToggleExpanded));
        menu.Items.Add("设置", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(showSettings));
        menu.Items.Add("关于与更新", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(showAbout));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(exit));
        return menu;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
