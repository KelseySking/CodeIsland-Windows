using CodeIsland.Core.Sources;

namespace CodeIsland.Core.Services;

/// <summary>
/// 将各 CLI 工具的事件名标准化为内部 PascalCase 名称
/// </summary>
public static class EventNormalizer
{
    /// <summary>
    /// 标准化事件名
    /// </summary>
    public static string NormalizeEventName(string source, string rawEventName)
    {
        var rawName = rawEventName.Trim();
        var adapter = CodeIslandSourceAdapterRegistry.Get(source);
        if (adapter.TryNormalizeEventName(rawName, out var sourceSpecific))
            return sourceSpecific;

        return rawName.ToLowerInvariant() switch
        {
            // 通用 snake_case / camelCase / PascalCase / lowercase 事件
            "permission_request" or "permissionrequest" => "PermissionRequest",
            "permission_denied" or "permissiondenied" => "PermissionDenied",
            "pre_tool_use" or "pretooluse" => "PreToolUse",
            "post_tool_use" or "posttooluse" => "PostToolUse",
            "post_tool_use_failure" or "posttoolusefailure" => "PostToolUseFailure",
            "user_prompt_submit" or "userpromptsubmit" => "UserPromptSubmit",
            "session_start" or "sessionstart" => "SessionStart",
            "session_end" or "sessionend" => "SessionEnd",
            "subagent_start" or "subagentstart" => "SubagentStart",
            "subagent_stop" or "subagentstop" => "SubagentStop",
            "pre_compact" or "precompact" => "PreCompact",
            "post_compact" or "postcompact" => "PostCompact",
            "stop" => "Stop",
            "notification" => "Notification",

            // 通用: 未识别事件按原始大小写透传
            _ => rawName
        };
    }

    /// <summary>
    /// 标准化字段名
    /// </summary>
    public static string NormalizeFieldName(string rawFieldName)
    {
        return rawFieldName switch
        {
            "hook_event_name" or "hookEventName" or "event_name" or "eventName" => "event_name",
            "session_id" or "sessionId" => "session_id",
            "tool_name" or "toolName" or "tool" => "tool_name",
            "tool_use_id" or "toolUseId" => "tool_use_id",
            "tool_input" or "toolInput" or "input" or "arguments" or "args" => "tool_input",
            _ => rawFieldName
        };
    }
}
