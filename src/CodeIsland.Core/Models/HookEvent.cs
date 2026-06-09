using System.Text.Json;

namespace CodeIsland.Core.Models;

/// <summary>
/// 从 AI 工具 Hook 接收的事件
/// </summary>
public class HookEvent
{
    public string EventName { get; init; } = "";
    public string? SessionId { get; init; }
    public string? ToolName { get; init; }
    public string? ToolUseId { get; init; }
    public string? AgentId { get; init; }
    public JsonElement? ToolInput { get; init; }
    public JsonElement RawJson { get; init; }

    /// <summary>
    /// Bridge 注入的来源标识
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Bridge 注入的父进程 PID
    /// </summary>
    public int? ParentPid { get; init; }

    /// <summary>
    /// Bridge 选择的生命周期跟踪 PID，用于终端关闭后的 session cleanup。
    /// </summary>
    public int? TrackedPid { get; init; }

    /// <summary>
    /// 跟踪 PID 的来源类型，例如 shell、cli 或 parent。
    /// </summary>
    public string? TrackedPidKind { get; init; }

    /// <summary>
    /// Bridge 采集到的跟踪进程启动时间，用于降低 PID 复用误判。
    /// </summary>
    public DateTime? TrackedProcessStartedAtUtc { get; init; }

    /// <summary>
    /// 从 JSON 解析 HookEvent，接受多种字段名变体
    /// </summary>
    public static HookEvent? FromJson(JsonElement json, string? source = null)
    {
        var eventName = GetStringField(json, "hook_event_name", "hookEventName", "event_name", "eventName", "event");
        if (string.IsNullOrEmpty(eventName)) return null;

        return new HookEvent
        {
            EventName = eventName,
            SessionId = GetStringField(json, "session_id", "sessionId"),
            ToolName = GetStringField(json, "tool_name", "toolName", "tool", "name"),
            ToolUseId = GetStringField(json, "tool_use_id", "toolUseId"),
            AgentId = GetStringField(json, "agent_id", "agentId"),
            ToolInput = GetNestedField(json, "tool_input", "toolInput", "input", "arguments", "args", "params"),
            RawJson = json.Clone(),
            Source = NormalizeSource(source) ?? NormalizeSource(GetStringField(json, "_source", "source", "CODEISLAND_SOURCE", "codeisland_source", "tool_source", "toolSource")),
            ParentPid = GetIntField(json, "_ppid", "_hook_ppid"),
            TrackedPid = GetIntField(json, "_tracked_pid"),
            TrackedPidKind = GetStringField(json, "_tracked_pid_kind"),
            TrackedProcessStartedAtUtc = GetDateTimeField(json, "_tracked_process_started_at_utc")
        };
    }

    private static string? GetStringField(JsonElement json, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (json.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }

        // 尝试嵌套查找
        foreach (var nestKey in new[] { "tool", "payload", "data", "env", "environment" })
        {
            if (json.TryGetProperty(nestKey, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                var result = GetStringField(nested, keys);
                if (result != null) return result;
            }
        }
        return null;
    }

    private static string? NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var normalized = source.Trim();
        return SupportedSource.IsValid(normalized) ? normalized : null;
    }

    private static int? GetIntField(JsonElement json, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (json.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
                return prop.GetInt32();
        }
        return null;
    }

    private static DateTime? GetDateTimeField(JsonElement json, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!json.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.String)
                continue;

            var value = prop.GetString();
            if (DateTimeOffset.TryParse(value, out var parsed))
                return parsed.UtcDateTime;
        }

        return null;
    }

    private static JsonElement? GetNestedField(JsonElement json, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (json.TryGetProperty(key, out var prop) && prop.ValueKind != JsonValueKind.Null)
                return prop.Clone();
        }
        return null;
    }
}
