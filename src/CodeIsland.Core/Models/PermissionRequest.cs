namespace CodeIsland.Core.Models;

/// <summary>
/// 权限请求模型
/// </summary>
public class PermissionRequest
{
    public string SessionId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string? ToolUseId { get; init; }
    public Dictionary<string, object?>? ToolInput { get; init; }
    public string? Description { get; init; }
    public string HookEventName { get; init; } = "PermissionRequest";

    /// <summary>
    /// 是否为安全的内部工具（可自动审批）
    /// </summary>
    public bool IsSafeInternalTool => ToolName is "Read" or "Grep" or "Glob" or "LS";
}
