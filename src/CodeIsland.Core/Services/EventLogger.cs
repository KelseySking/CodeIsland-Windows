using System.Text;

namespace CodeIsland.Core.Services;

/// <summary>
/// 轻量级 hook 事件诊断日志：管道分隔字段、线程安全、按大小轮转。
/// 用于排查 HUD 状态机问题，落地在 %APPDATA%\CodeIsland\hook.log。
/// </summary>
public sealed class EventLogger
{
    private readonly string _logPath;
    private readonly string _rotatedPath;
    private readonly long _maxBytes;
    private readonly object _writeLock = new();

    public EventLogger(string? logDir = null, long maxBytes = 1_048_576)
    {
        var dir = logDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeIsland");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "hook.log");
        _rotatedPath = Path.Combine(dir, "hook.log.1");
        _maxBytes = maxBytes;
    }

    public string LogPath => _logPath;

    /// <summary>
    /// 写一行日志。字段顺序固定：timestamp|category|message|key1=val1|key2=val2|...
    /// 不抛异常 —— 诊断日志失败不能影响主流程。
    /// </summary>
    public void Write(string category, string message, IReadOnlyDictionary<string, string?>? fields = null)
    {
        try
        {
            var sb = new StringBuilder(256);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append('|').Append(category);
            sb.Append('|').Append(Escape(message));
            if (fields != null)
            {
                foreach (var kv in fields)
                {
                    sb.Append('|').Append(kv.Key).Append('=').Append(Escape(kv.Value ?? ""));
                }
            }
            sb.Append('\n');

            lock (_writeLock)
            {
                RotateIfNeededLocked();
                File.AppendAllText(_logPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 静默吞掉 —— 日志失败不能影响 hook 主流程
        }
    }

    private void RotateIfNeededLocked()
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length < _maxBytes) return;
            if (File.Exists(_rotatedPath)) File.Delete(_rotatedPath);
            File.Move(_logPath, _rotatedPath);
        }
        catch
        {
            // 轮转失败时让 AppendAllText 自己处理
        }
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOfAny(['|', '\n', '\r']) < 0) return value;
        return value
            .Replace("\\", "\\\\")
            .Replace("|", "\\|")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }
}
