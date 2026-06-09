namespace CodeIsland.Core.Models;

/// <summary>
/// 支持的 AI 编程工具来源定义
/// </summary>
public static class SupportedSource
{
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude", "codex", "gemini", "cursor", "cursor-cli",
        "trae", "traecn", "traecli", "copilot",
        "qoder", "qoder-cli", "droid", "codebuddy", "codybuddycn",
        "stepfun", "opencode", "antigravity", "workbuddy",
        "hermes", "qwen", "kimi", "pi", "kiro", "cline"
    };

    /// <summary>
    /// 来源对应的显示名称
    /// </summary>
    public static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "Claude Code",
        ["codex"] = "Codex",
        ["gemini"] = "Gemini CLI",
        ["cursor"] = "Cursor",
        ["cursor-cli"] = "Cursor CLI",
        ["trae"] = "Trae",
        ["traecn"] = "Trae CN",
        ["traecli"] = "TraeCli",
        ["copilot"] = "GitHub Copilot",
        ["qoder"] = "Qoder",
        ["qoder-cli"] = "Qoder CLI",
        ["droid"] = "Factory",
        ["codebuddy"] = "CodeBuddy",
        ["codybuddycn"] = "CodyBuddy CN",
        ["stepfun"] = "StepFun",
        ["opencode"] = "OpenCode",
        ["antigravity"] = "AntiGravity",
        ["workbuddy"] = "WorkBuddy",
        ["hermes"] = "Hermes",
        ["qwen"] = "Qwen Code",
        ["kimi"] = "Kimi Code",
        ["pi"] = "Pi",
        ["kiro"] = "Kiro",
        ["cline"] = "Cline",
        ["unknown"] = "未知工具",
        ["codeisland"] = "CodeIsland"
    };

    /// <summary>
    /// 来源对应的图标文件名（不含扩展名）
    /// </summary>
    public static readonly Dictionary<string, string> IconNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "claude",
        ["codex"] = "codex",
        ["gemini"] = "gemini",
        ["cursor"] = "cursor",
        ["cursor-cli"] = "cursor",
        ["trae"] = "trae",
        ["traecn"] = "trae",
        ["traecli"] = "traecli",
        ["copilot"] = "copilot",
        ["qoder"] = "qoder",
        ["qoder-cli"] = "qoder",
        ["droid"] = "factory",
        ["codebuddy"] = "codebuddy",
        ["codybuddycn"] = "codebuddy",
        ["stepfun"] = "stepfun",
        ["opencode"] = "opencode",
        ["antigravity"] = "antigravity",
        ["workbuddy"] = "workbuddy",
        ["hermes"] = "hermes",
        ["qwen"] = "qwen",
        ["kimi"] = "kimi",
        ["pi"] = "pi",
        ["kiro"] = "kiro",
        ["cline"] = "cline"
    };

    public static bool IsValid(string source) => All.Contains(source);

    public static string GetDisplayName(string source) =>
        DisplayNames.TryGetValue(source, out var name) ? name : source;

    public static string GetIconName(string source) =>
        IconNames.TryGetValue(source, out var icon) ? icon : source;
}
