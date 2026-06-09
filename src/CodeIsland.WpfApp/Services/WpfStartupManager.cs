using System.IO;
using Microsoft.Win32;

namespace CodeIsland.WpfApp.Services;

public static class WpfStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodeIsland";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, out string message)
    {
        message = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            message = "当前平台不支持开机自启设置";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                message = "无法打开 Windows 启动项注册表";
                return false;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) + ".exe";
                key.SetValue(ValueName, $"\"{exePath}\"");
                message = "已启用开机自启";
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                message = "已关闭开机自启";
            }

            return true;
        }
        catch (Exception ex)
        {
            message = $"开机自启设置失败：{ex.Message}";
            return false;
        }
    }
}
