using System.Diagnostics;
using System.Runtime.InteropServices;
using CodeIsland.WpfApp.Models;

namespace CodeIsland.WpfApp.Services;

public static class WpfTerminalActivator
{
    public static bool Activate(SessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(session.TerminalSessionId) && WindowsTerminalActivator.ActivateBySession(session.TerminalSessionId))
            return true;

        if (session.Pid > 0 && GenericTerminalActivator.ActivateByPid(session.Pid))
            return true;

        return WindowsTerminalActivator.BringToFront();
    }

    private static class WindowsTerminalActivator
    {
        public static bool ActivateBySession(string wtSessionId)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("WindowsTerminal"))
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;

                    NativeMethods.ShowWindow(process.MainWindowHandle, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(process.MainWindowHandle);
                    return true;
                }
            }
            catch
            {
                // 终端查找失败时静默降级到下一策略。
            }

            return false;
        }

        public static bool BringToFront()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("WindowsTerminal"))
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;

                    NativeMethods.ShowWindow(process.MainWindowHandle, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(process.MainWindowHandle);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }

    private static class GenericTerminalActivator
    {
        public static bool ActivateByPid(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.MainWindowHandle == IntPtr.Zero)
                    return false;

                NativeMethods.ShowWindow(process.MainWindowHandle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(process.MainWindowHandle);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
