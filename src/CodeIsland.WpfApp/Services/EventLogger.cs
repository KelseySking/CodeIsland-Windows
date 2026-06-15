using System.IO;
using System.Text;

namespace CodeIsland.WpfApp.Services;

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
                    sb.Append('|').Append(kv.Key).Append('=').Append(Escape(kv.Value ?? ""));
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
        }
    }

    private void RotateIfNeededLocked()
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length < _maxBytes)
                return;
            if (File.Exists(_rotatedPath))
                File.Delete(_rotatedPath);
            File.Move(_logPath, _rotatedPath);
        }
        catch
        {
        }
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value.IndexOfAny(['|', '\n', '\r']) < 0)
            return value;
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
