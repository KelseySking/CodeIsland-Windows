using System.Text.Json;
using CodeIsland.Core.Models;

namespace CodeIsland.Bridge;

/// <summary>
/// 从进程族谱中识别 AI 工具来源
/// </summary>
public static class SourceResolver
{
    private static readonly Dictionary<string, string> ExeToSource = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "claude",
        ["codex"] = "codex",
        ["gemini"] = "gemini",
        ["cursor"] = "cursor",
        ["code"] = "vscode",
        ["copilot"] = "copilot",
        ["qoder"] = "qoder",
        ["factory"] = "droid",
        ["codebuddy"] = "codebuddy",
        ["opencode"] = "opencode",
        ["cline"] = "cline",
        ["node"] = "node", // 可能是 opencode 等 Node.js 工具
    };

    // Cursor CLI Agent 变体提升
    private static readonly HashSet<string> CursorCliIndicators = new(StringComparer.OrdinalIgnoreCase)
    {
        "cursor-agent", "cursor-agent.exe"
    };

    /// <summary>
    /// 从进程族谱推断来源
    /// </summary>
    public static string InferSource(List<ProcessInfo> ancestry, string? explicitSource = null, JsonElement? payload = null)
    {
        // 显式指定的来源优先
        if (NormalizeSource(explicitSource) is { } normalizedExplicit)
            return normalizedExplicit;

        foreach (var proc in ancestry)
        {
            var name = Path.GetFileNameWithoutExtension(proc.Name);

            // 检查 Cursor CLI 变体
            if (CursorCliIndicators.Contains(name))
                return "cursor-cli";

            if (ExeToSource.TryGetValue(name, out var source))
            {
                // 特殊处理: node 进程需要进一步检查命令行参数
                if (source == "node")
                {
                    var inferred = InferFromNodeProcess(proc);
                    if (inferred != null) return inferred;
                    continue;
                }
                return source;
            }
        }

        return ExtractSourceFromPayload(payload) ?? "unknown";
    }

    private static string? ExtractSourceFromPayload(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        var direct = GetStringField(obj, "_source", "source", "CODEISLAND_SOURCE", "codeisland_source", "tool_source", "toolSource");
        if (NormalizeSource(direct) is { } source)
            return source;

        var transcriptPath = GetStringField(obj, "transcript_path", "transcriptPath");
        if (transcriptPath?.Contains(".claude", StringComparison.OrdinalIgnoreCase) == true)
            return "claude";

        foreach (var nestKey in new[] { "env", "environment", "payload", "data" })
        {
            if (obj.TryGetProperty(nestKey, out var nested) && nested.ValueKind == JsonValueKind.Object &&
                ExtractSourceFromPayload(nested) is { } nestedSource)
                return nestedSource;
        }

        return null;
    }

    private static string? GetStringField(JsonElement json, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (json.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
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

    /// <summary>
    /// 从 Node.js 进程推断具体工具
    /// </summary>
    private static string? InferFromNodeProcess(ProcessInfo proc)
    {
        // 检查可执行路径中是否包含已知工具名
        var path = proc.ExecutablePath.ToLowerInvariant();
        if (path.Contains("opencode")) return "opencode";
        if (path.Contains("cline")) return "cline";
        return null;
    }
}
