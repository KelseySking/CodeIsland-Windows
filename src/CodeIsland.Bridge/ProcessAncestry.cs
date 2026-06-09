using System.Management;

namespace CodeIsland.Bridge;

/// <summary>
/// 进程信息
/// </summary>
public record ProcessInfo(int Pid, int ParentPid, string Name, string ExecutablePath, DateTime? StartedAtUtc = null);

/// <summary>
/// 通过 WMI 查询进程族谱
/// </summary>
public static class ProcessAncestry
{
    /// <summary>
    /// 向上遍历进程族谱，返回从当前进程到根的路径列表
    /// </summary>
    public static List<ProcessInfo> BuildAncestry(int startingPid, int maxDepth = 12)
    {
        var ancestry = new List<ProcessInfo>();
        var pid = startingPid;

        for (int i = 0; i < maxDepth && pid > 0; i++)
        {
            var info = GetProcessInfo(pid);
            if (info == null) break;
            ancestry.Add(info);
            pid = info.ParentPid;
        }
        return ancestry;
    }

    /// <summary>
    /// 通过 WMI 查询单个进程信息
    /// </summary>
    public static ProcessInfo? GetProcessInfo(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CreationDate FROM Win32_Process WHERE ProcessId = {pid}");

            foreach (ManagementObject obj in searcher.Get())
            {
                return new ProcessInfo(
                    Pid: Convert.ToInt32(obj["ProcessId"]),
                    ParentPid: Convert.ToInt32(obj["ParentProcessId"]),
                    Name: obj["Name"]?.ToString() ?? "",
                    ExecutablePath: obj["ExecutablePath"]?.ToString() ?? "",
                    StartedAtUtc: ParseWmiDateTime(obj["CreationDate"]?.ToString())
                );
            }
        }
        catch { }
        return null;
    }

    private static DateTime? ParseWmiDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            if (value.Length < 25 ||
                value[14] != '.' ||
                !int.TryParse(value[..4], out var year) ||
                !int.TryParse(value.Substring(4, 2), out var month) ||
                !int.TryParse(value.Substring(6, 2), out var day) ||
                !int.TryParse(value.Substring(8, 2), out var hour) ||
                !int.TryParse(value.Substring(10, 2), out var minute) ||
                !int.TryParse(value.Substring(12, 2), out var second) ||
                !int.TryParse(value.Substring(15, 6), out var microsecond) ||
                !int.TryParse(value.Substring(22, 3), out var offsetMinutes))
            {
                return null;
            }

            var offset = TimeSpan.FromMinutes(value[21] == '-' ? -offsetMinutes : offsetMinutes);
            var timestamp = new DateTimeOffset(year, month, day, hour, minute, second, offset)
                .AddTicks(microsecond * 10L);
            return timestamp.UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取当前进程的父进程 PID
    /// </summary>
    public static int GetParentPid()
    {
        var currentPid = Environment.ProcessId;
        var info = GetProcessInfo(currentPid);
        return info?.ParentPid ?? 0;
    }
}
