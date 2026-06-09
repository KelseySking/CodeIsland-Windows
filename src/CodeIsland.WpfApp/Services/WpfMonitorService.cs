using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;

namespace CodeIsland.WpfApp.Services;

public sealed record WpfMonitorOption(string Id, string DisplayName, bool IsPrimary, Rect BoundsPhysical, Rect WorkAreaPhysical, DpiScale Dpi)
{
    public Rect WorkAreaDip => WpfMonitorService.PhysicalRectToDipRect(WorkAreaPhysical, Dpi);
}

public static class WpfMonitorService
{
    public const string AutoMonitorId = "auto";
    private const int DeviceNameLength = 32;
    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MONITORINFOF_PRIMARY = 1;

    public static IReadOnlyList<WpfMonitorOption> GetMonitors()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var monitors = new List<WpfMonitorOption>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (TryCreateMonitorOption(monitor, monitors.Count, out var option))
                monitors.Add(option);
            return true;
        }, IntPtr.Zero);

        return monitors.Count > 0 ? monitors : [];
    }

    public static WpfMonitorOption? ResolveMonitor(string? configuredMonitorId, Window? window = null)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(configuredMonitorId)
            && !string.Equals(configuredMonitorId, AutoMonitorId, StringComparison.OrdinalIgnoreCase))
        {
            var selected = monitors.FirstOrDefault(monitor => string.Equals(monitor.Id, configuredMonitorId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
                return selected;

            // Device paths can survive position changes better than coordinate IDs.
            var configuredDevice = configuredMonitorId.Split('|')[0];
            selected = monitors.FirstOrDefault(monitor => monitor.Id.StartsWith(configuredDevice + "|", StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
                return selected;
        }

        return GetMouseMonitor(monitors)
            ?? GetWindowMonitor(window, monitors)
            ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? monitors[0];
    }

    public static WpfMonitorOption? GetMonitorFromPhysicalRect(Rect physicalRect)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var rect = new RECT
        {
            Left = (int)Math.Round(physicalRect.Left),
            Top = (int)Math.Round(physicalRect.Top),
            Right = (int)Math.Round(physicalRect.Right),
            Bottom = (int)Math.Round(physicalRect.Bottom)
        };
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        return monitor != IntPtr.Zero && TryCreateMonitorOption(monitor, 0, out var option) ? option : null;
    }

    public static Rect PhysicalRectToDipRect(Rect rect, DpiScale dpi)
    {
        var scaleX = dpi.DpiScaleX > 0d ? dpi.DpiScaleX : 1d;
        var scaleY = dpi.DpiScaleY > 0d ? dpi.DpiScaleY : 1d;
        return new Rect(rect.Left / scaleX, rect.Top / scaleY, Math.Max(0d, rect.Width) / scaleX, Math.Max(0d, rect.Height) / scaleY);
    }

    private static WpfMonitorOption? GetMouseMonitor(IReadOnlyList<WpfMonitorOption> monitors)
    {
        if (!GetCursorPos(out var point))
            return null;

        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero || !TryCreateMonitorOption(monitor, 0, out var option))
            return null;

        return monitors.FirstOrDefault(candidate => string.Equals(candidate.Id, option.Id, StringComparison.OrdinalIgnoreCase)) ?? option;
    }

    private static WpfMonitorOption? GetWindowMonitor(Window? window, IReadOnlyList<WpfMonitorOption> monitors)
    {
        if (window is null)
            return null;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return null;

        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || !TryCreateMonitorOption(monitor, 0, out var option))
            return null;

        return monitors.FirstOrDefault(candidate => string.Equals(candidate.Id, option.Id, StringComparison.OrdinalIgnoreCase)) ?? option;
    }

    private static bool TryCreateMonitorOption(IntPtr monitor, int index, out WpfMonitorOption option)
    {
        option = default!;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        var dpi = TryGetMonitorDpiScale(monitor, out var monitorDpi) ? monitorDpi : new DpiScale(1d, 1d);
        var bounds = ToRect(info.rcMonitor);
        var workArea = ToRect(info.rcWork);
        var deviceName = info.szDevice?.TrimEnd('\0') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = $"Monitor{index + 1}";

        var id = $"{deviceName}|{(int)bounds.Left},{(int)bounds.Top},{(int)bounds.Right},{(int)bounds.Bottom}";
        var isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) == MONITORINFOF_PRIMARY;
        var displayName = $"显示器 {index + 1}{(isPrimary ? "（主屏）" : "")} {FormatBounds(bounds)}";
        option = new WpfMonitorOption(id, displayName, isPrimary, bounds, workArea, dpi);
        return true;
    }

    private static string FormatBounds(Rect bounds) => $"{(int)bounds.Width}×{(int)bounds.Height} @ {(int)bounds.Left},{(int)bounds.Top}";

    private static Rect ToRect(RECT rect) => new(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));

    private static bool TryGetMonitorDpiScale(IntPtr monitor, out DpiScale dpi)
    {
        dpi = default;
        if (GetDpiForMonitor(monitor, MonitorDpiType.EffectiveDpi, out var dpiX, out var dpiY) != 0 || dpiX == 0 || dpiY == 0)
            return false;

        dpi = new DpiScale(dpiX / 96d, dpiY / 96d);
        return true;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    private enum MonitorDpiType
    {
        EffectiveDpi = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceNameLength)]
        public string szDevice;
    }
}
