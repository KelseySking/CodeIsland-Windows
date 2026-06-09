namespace CodeIsland.Bridge;

/// <summary>
/// 采集 Windows 终端环境变量
/// </summary>
public static class EnvironmentCollector
{
    private static readonly string[] EnvKeys =
    [
        "WT_SESSION",           // Windows Terminal 标签页 GUID
        "TERM_PROGRAM",         // 终端程序名称
        "TERMINAL_EMULATOR",    // JetBrains JediTerm 等集成终端标识
        "TERM_SESSION_ID",      // iTerm2 (WSL)
        "KITTY_WINDOW_ID",      // Kitty (WSL)
        "TMUX",                 // tmux
        "TMUX_PANE",            // tmux 面板
        "WEZTERM_PANE",         // WezTerm 面板
        "VSCODE_INJECTION",     // VS Code 集成终端
        "VSCODE_GIT_IPC_HANDLE",// VS Code Git IPC
        "ConEmuPID",            // ConEmu
        "ANSICON",              // ANSICON
        "MSYSTEM",              // MSYS2
        "SHELL",                // Shell 路径
        "TERM",                 // 终端类型
        "COLORTERM",            // 颜色终端类型
        "WT_PROFILE_ID",        // Windows Terminal 配置 ID
    ];

    /// <summary>
    /// 采集所有相关的终端环境变量
    /// </summary>
    public static Dictionary<string, string> Collect()
    {
        var result = new Dictionary<string, string>();

        foreach (var key in EnvKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
                result[key] = value;
        }

        // 控制台标题（Windows 特有，替代 TTY 检测）
        try
        {
            result["_console_title"] = Console.Title;
        }
        catch { }

        // 当前工作目录
        try
        {
            result["_cwd"] = Environment.CurrentDirectory;
        }
        catch { }

        return result;
    }

    /// <summary>
    /// 将环境变量注入到 JSON 字典中
    /// 环境变量键名转小写，已有 _ 前缀的保持不变
    /// </summary>
    public static void InjectIntoPayload(Dictionary<string, object?> payload)
    {
        InjectIntoPayload(payload, Collect());
    }

    /// <summary>
    /// 将已采集的环境变量注入到 JSON 字典中，避免同一次 Bridge 调用重复读取环境
    /// </summary>
    public static void InjectIntoPayload(Dictionary<string, object?> payload, IReadOnlyDictionary<string, string> env)
    {
        foreach (var kvp in env)
        {
            var key = kvp.Key.ToLowerInvariant();
            if (!key.StartsWith('_'))
                key = $"_{key}";
            payload[key] = kvp.Value;

            // WT_SESSION 是后续终端激活的关键字段，同时保留原始大写键，
            // 便于 Core/App 与外部工具使用同一个约定。
            if (kvp.Key == "WT_SESSION" && !payload.ContainsKey("WT_SESSION"))
                payload["WT_SESSION"] = kvp.Value;
        }
    }
}
