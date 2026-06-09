using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace CodeIsland.WpfApp.Services;

public static class WpfFullscreenDetector
{
    private const int ClassNameBufferLength = 256;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private static readonly string[] DesktopShellClassNames =
    {
        "Progman",
        "WorkerW",
        "SHELLDLL_DefView"
    };

    private static readonly string[] WallpaperHostProcessNames =
    {
        "wallpaper32",
        "wallpaper64",
        "wallpaperengine",
        "applicationwallpaper"
    };

    public static bool IsForegroundFullscreen(Rect screenBounds, IntPtr excludedWindow)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        if (!TryGetForegroundWindowInfo(foreground, out var window))
            return false;

        return IsFullscreenCandidate(window, screenBounds, excludedWindow);
    }

    internal static bool IsFullscreenCandidate(ForegroundWindowInfo window, Rect screenBounds, IntPtr excludedWindow)
    {
        if (window.Handle == IntPtr.Zero || window.Handle == excludedWindow)
            return false;

        if (!window.IsVisible || window.IsMinimized)
            return false;

        if (IsDesktopOrWallpaperHost(window.ClassName, window.ProcessName))
            return false;

        return CoversScreen(window.Bounds, screenBounds);
    }

    internal static bool IsDesktopOrWallpaperHost(string? className, string? processName)
    {
        if (!string.IsNullOrWhiteSpace(className))
        {
            foreach (var desktopClassName in DesktopShellClassNames)
            {
                if (string.Equals(className.Trim(), desktopClassName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        var normalizedProcessName = NormalizeProcessName(processName);
        if (!string.IsNullOrEmpty(normalizedProcessName))
        {
            foreach (var wallpaperHostProcessName in WallpaperHostProcessNames)
            {
                if (string.Equals(normalizedProcessName, wallpaperHostProcessName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool TryGetForegroundWindowInfo(IntPtr foreground, out ForegroundWindowInfo window)
    {
        window = default;
        if (!GetWindowRect(foreground, out var rect))
            return false;

        window = new ForegroundWindowInfo(
            foreground,
            new Rect(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top)),
            GetWindowClassName(foreground),
            GetProcessName(foreground),
            IsWindowVisible(foreground),
            IsIconic(foreground));

        return true;
    }

    private static bool CoversScreen(Rect windowBounds, Rect screenBounds)
    {
        const double tolerance = 2;
        return windowBounds.Left <= screenBounds.Left + tolerance &&
               windowBounds.Top <= screenBounds.Top + tolerance &&
               windowBounds.Right >= screenBounds.Right - tolerance &&
               windowBounds.Bottom >= screenBounds.Bottom - tolerance;
    }

    private static string? GetWindowClassName(IntPtr hWnd)
    {
        var buffer = new StringBuilder(ClassNameBufferLength);
        var length = GetClassName(hWnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : null;
    }

    private static string? GetProcessName(IntPtr hWnd)
    {
        _ = GetWindowThreadProcessId(hWnd, out var processId);
        if (processId == 0)
            return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        var normalized = processName.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        return normalized;
    }

    public static Rect GetWindowScreenBounds(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(helper.Handle, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var rect = info.rcMonitor;
                    return new Rect(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
                }
            }
        }

        var source = helper.Handle == IntPtr.Zero ? null : HwndSource.FromHwnd(helper.Handle);
        var transform = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var width = SystemParameters.PrimaryScreenWidth * transform.M11;
        var height = SystemParameters.PrimaryScreenHeight * transform.M22;
        return new Rect(0, 0, width, height);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    internal readonly record struct ForegroundWindowInfo(
        IntPtr Handle,
        Rect Bounds,
        string? ClassName,
        string? ProcessName,
        bool IsVisible,
        bool IsMinimized);
}
