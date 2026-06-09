using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIsland.Core.Services;

/// <summary>
/// Hook 配置安装服务，支持 5 种 Hook 格式
/// </summary>
public static class ConfigInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const string HookScriptName = "codeisland-hook.ps1";

    private const string DeployedBridgeExeName = "codeisland-bridge.exe";

    private const string ReleaseBridgeExeName = "CodeIsland.Bridge.exe";

    private const string UserProfileOverrideEnvironmentVariable = "CODEISLAND_TEST_USERPROFILE";

    private const string BridgeSourceOverrideEnvironmentVariable = "CODEISLAND_BRIDGE_SOURCE_PATH";

    private const string BridgeSourceTestOverrideEnvironmentVariable = "CODEISLAND_TEST_BRIDGE_SOURCE_PATH";

    private static string UserProfileDirectory =>
        Environment.GetEnvironmentVariable(UserProfileOverrideEnvironmentVariable) is { Length: > 0 } overridePath
            ? overridePath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string CodeIslandDir =>
        Path.Combine(UserProfileDirectory, ".codeisland");

    private static string HookScriptPath =>
        Path.Combine(CodeIslandDir, HookScriptName);

    public static string RuntimeDirectory => CodeIslandDir;

    public static string RuntimeHookScriptPath => HookScriptPath;

    public static string RuntimeBridgeExePath => BridgeExePath;

    private static string BridgeExePath =>
        Path.Combine(CodeIslandDir, DeployedBridgeExeName);

    /// <summary>
    /// Hook 格式枚举
    /// </summary>
    private enum HookFormat
    {
        Claude,   // Claude Code: settings.json 中的 hooks 键
        Codex,    // Codex CLI: hooks.json，每事件 { hooks: [{ type, command, timeout }] } 包裹层，无 matcher
        Nested,   // Gemini: hooks.json 嵌套结构（扁平 { command, timeout } entry）
        Flat,     // Cursor, Trae: 扁平 [{command}] 数组
        Copilot,  // GitHub Copilot CLI: 带 version 的 hook 数组
        Cline     // Cline (VS Code): 每事件独立可执行文件
    }

    /// <summary>
    /// Source 到 Hook 格式的映射
    /// </summary>
    private static readonly Dictionary<string, HookFormat> SourceFormatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = HookFormat.Claude,
        ["codex"] = HookFormat.Codex,
        ["gemini"] = HookFormat.Nested,
        ["cursor"] = HookFormat.Flat,
        ["cursor-cli"] = HookFormat.Flat,
        ["trae"] = HookFormat.Flat,
        ["traecn"] = HookFormat.Flat,
        ["traecli"] = HookFormat.Flat,
        ["copilot"] = HookFormat.Copilot,
        ["cline"] = HookFormat.Cline
    };

    /// <summary>
    /// Hook 事件名称列表
    /// </summary>
    private static readonly string[] HookEvents =
    [
        "PreToolUse", "PostToolUse", "UserPromptSubmit",
        "SessionStart", "SessionEnd", "Stop", "Notification"
    ];

    private static readonly string[] ClaudeHookEvents =
    [
        "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
        "PermissionRequest", "Stop", "SubagentStart", "SubagentStop",
        "SessionStart", "SessionEnd", "Notification", "PreCompact"
    ];

    /// <summary>
    /// Codex CLI 的事件集与 timeout（秒）。对齐 macOS 参考实现 Codex CLIConfig：
    /// PreToolUse 可能因显式 approval-needed 信号阻塞，PermissionRequest 在 shell 升级/网络审批前触发，
    /// 二者都必须用长 timeout 等待用户决定，否则面板会卡在 running 或 CLI 提前超时。
    /// Codex 不使用通用 HookEvents 中的 Notification。
    /// </summary>
    private static readonly (string Event, int Timeout)[] CodexHookEvents =
    [
        ("SessionStart", 5),
        ("SessionEnd", 5),
        ("UserPromptSubmit", 5),
        ("PreToolUse", 86400),
        ("PostToolUse", 5),
        ("PermissionRequest", 86400),
        ("Stop", 5)
    ];

    /// <summary>
    /// 获取所有支持的 source 列表
    /// </summary>
    public static IReadOnlyList<string> SupportedSources => SourceFormatMap.Keys.ToList();

    /// <summary>
    /// 安装 hook 到指定 AI 工具
    /// </summary>
    public static bool Install(string source)
    {
        if (!SourceFormatMap.TryGetValue(source, out var format))
            return false;

        try
        {
            var runtimeAssetsReady = RepairRuntimeAssets();
            var configInstalled = format switch
            {
                HookFormat.Claude => InstallClaude(),
                HookFormat.Codex => InstallCodex(),
                HookFormat.Nested => InstallNested(source),
                HookFormat.Flat => InstallFlat(source),
                HookFormat.Copilot => InstallCopilot(),
                HookFormat.Cline => InstallCline(),
                _ => false
            };
            return configInstalled && runtimeAssetsReady;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 卸载 hook
    /// </summary>
    public static bool Uninstall(string source)
    {
        if (!SourceFormatMap.TryGetValue(source, out var format))
            return false;

        try
        {
            return format switch
            {
                HookFormat.Claude => UninstallClaude(),
                HookFormat.Codex => UninstallCodex(),
                HookFormat.Nested => UninstallNested(source),
                HookFormat.Flat => UninstallFlat(source),
                HookFormat.Copilot => UninstallCopilot(),
                HookFormat.Cline => UninstallCline(),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检测是否已安装
    /// </summary>
    public static bool IsInstalled(string source)
    {
        if (!SourceFormatMap.TryGetValue(source, out var format))
            return false;

        try
        {
            if (!AreRuntimeAssetsInstalled())
                return false;

            return format switch
            {
                HookFormat.Claude => IsInstalledClaude(),
                HookFormat.Codex => IsInstalledCodex(),
                HookFormat.Nested => IsInstalledNested(source),
                HookFormat.Flat => IsInstalledFlat(source),
                HookFormat.Copilot => IsInstalledCopilot(),
                HookFormat.Cline => IsInstalledCline(),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取所有支持的 source 的安装状态
    /// </summary>
    public static Dictionary<string, bool> GetInstallStatuses()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in SourceFormatMap.Keys)
        {
            result[source] = IsInstalled(source);
        }
        return result;
    }

    // ============================================================
    // Runtime assets / Hook 脚本生成
    // ============================================================

    /// <summary>
    /// 修复 hook 运行时资产：部署 Bridge CLI，并重写指向运行时目录的 PowerShell hook 脚本。
    /// </summary>
    public static bool RepairRuntimeAssets()
    {
        try
        {
            Directory.CreateDirectory(CodeIslandDir);
            var bridgeReady = EnsureBridgeExecutable();
            EnsureHookScript();
            return bridgeReady && AreRuntimeAssetsInstalled();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 修复已经安装过的 CodeIsland hook 配置。用于应用启动时把旧版本缺失的事件补齐，
    /// 同时保持用户自己的 hook 条目不变。
    /// </summary>
    public static bool RepairInstalledHookConfigurations()
    {
        try
        {
            var runtimeAssetsReady = RepairRuntimeAssets();
            var configRepaired = true;

            if (HasAnyCodeIslandClaudeHook())
                configRepaired &= InstallClaude();

            return runtimeAssetsReady && configRepaired;
        }
        catch
        {
            return false;
        }
    }

    public static bool AreRuntimeAssetsInstalled() =>
        File.Exists(HookScriptPath) && File.Exists(BridgeExePath);

    private static bool EnsureBridgeExecutable()
    {
        var source = LocateBridgeExecutable();
        if (source == null)
            return File.Exists(BridgeExePath);

        if (Path.GetFullPath(source).Equals(Path.GetFullPath(BridgeExePath), StringComparison.OrdinalIgnoreCase))
            return File.Exists(BridgeExePath);

        if (File.Exists(BridgeExePath))
        {
            var sourceInfo = new FileInfo(source);
            var destinationInfo = new FileInfo(BridgeExePath);
            if (destinationInfo.Length == sourceInfo.Length && destinationInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
            {
                CopyBridgeSidecarFiles(source);
                return true;
            }
        }

        Directory.CreateDirectory(CodeIslandDir);
        File.Copy(source, BridgeExePath, overwrite: true);
        CopyBridgeSidecarFiles(source);
        return true;
    }

    private static void CopyBridgeSidecarFiles(string sourceBridgePath)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceBridgePath);
        if (sourceDirectory == null)
            return;

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals(ReleaseBridgeExeName, StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(DeployedBridgeExeName, StringComparison.OrdinalIgnoreCase))
                continue;

            var extension = Path.GetExtension(fileName);
            if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
                continue;

            File.Copy(file, Path.Combine(CodeIslandDir, fileName), overwrite: true);
        }
    }

    private static string? LocateBridgeExecutable()
    {
        var testOverridePath = Environment.GetEnvironmentVariable(BridgeSourceTestOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(testOverridePath))
            return File.Exists(testOverridePath) ? testOverridePath : null;

        var overridePath = Environment.GetEnvironmentVariable(BridgeSourceOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var baseDirectory = AppContext.BaseDirectory;
        foreach (var candidate in EnumerateBridgeCandidates(baseDirectory))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateBridgeCandidates(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory != null)
        {
            yield return Path.Combine(directory.FullName, ReleaseBridgeExeName);
            yield return Path.Combine(directory.FullName, DeployedBridgeExeName);

            yield return Path.Combine(directory.FullName, "src", "CodeIsland.Bridge", "bin", "Release", "net8.0", "win-x64", "publish", ReleaseBridgeExeName);
            yield return Path.Combine(directory.FullName, "src", "CodeIsland.Bridge", "bin", "Debug", "net8.0", "win-x64", "publish", ReleaseBridgeExeName);
            yield return Path.Combine(directory.FullName, "src", "CodeIsland.Bridge", "bin", "Release", "net8.0", "win-x64", ReleaseBridgeExeName);
            yield return Path.Combine(directory.FullName, "src", "CodeIsland.Bridge", "bin", "Debug", "net8.0", "win-x64", ReleaseBridgeExeName);
            yield return Path.Combine(directory.FullName, "src", "CodeIsland.Bridge", "bin", "Release", "net8.0", ReleaseBridgeExeName);
            yield return Path.Combine(directory.FullName, "src", "CodeIsland.Bridge", "bin", "Debug", "net8.0", ReleaseBridgeExeName);

            directory = directory.Parent;
        }
    }

    private static void EnsureHookScript()
    {
        Directory.CreateDirectory(CodeIslandDir);

        var bridgePath = BridgeExePath;
        var script = $$"""
            $bridge = "{{bridgePath}}"
            if (Test-Path $bridge) {
                $input | & $bridge @args
            } else {
                Write-Error "CodeIsland Bridge executable is missing: $bridge"
                exit 0
            }
            """;

        File.WriteAllText(HookScriptPath, script);
    }

    private static string GetHookCommand(string? source = null)
    {
        // 直接调用 bridge.exe，避免 PowerShell `$input` 中转破坏二进制 stdin（CRLF/CR、JSON 转义等）。
        // Claude Code / Codex 等都把 hook command 作为 shell 字符串执行，Windows 上 cmd /c 会把它交给 cmd 解释，
        // bridge.exe 是 PE 可执行文件，直接拼路径即可，stdin 由调用方原样喂入。
        var command = $"\"{BridgeExePath}\"";
        return string.IsNullOrWhiteSpace(source) ? command : $"{command} --source {source}";
    }

    private static bool IsCodeIslandCommand(JsonNode? node)
    {
        if (node?["command"] is not JsonValue commandNode || !commandNode.TryGetValue<string>(out var command))
            return false;

        return command.Contains(HookScriptName, StringComparison.OrdinalIgnoreCase) ||
               command.Contains("codeisland-bridge.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodeIslandCommandObject(JsonNode? node) =>
        node is JsonObject && IsCodeIslandCommand(node);

    private static void RemoveMatching(JsonArray array, Func<JsonNode?, bool> predicate)
    {
        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (predicate(array[i]))
                array.RemoveAt(i);
        }
    }

    private static bool JsonObjectHasValues(JsonObject json) => json.Any(static kvp => kvp.Value != null);

    // ============================================================
    // .claude 格式 (Claude Code)
    // ============================================================

    private static string GetClaudeSettingsPath() =>
        Path.Combine(UserProfileDirectory, ".claude", "settings.json");

    private static bool InstallClaude()
    {
        var path = GetClaudeSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = ReadOrCreateJson(path);
        var hooks = json["hooks"] as JsonObject ?? new JsonObject();

        foreach (var evt in ClaudeHookEvents)
            MergeClaudeHookEvent(hooks, evt, "claude");

        json["hooks"] = hooks;
        WriteJson(path, json);
        return true;
    }

    private static bool UninstallClaude()
    {
        var path = GetClaudeSettingsPath();
        if (!File.Exists(path)) return true;

        var json = ReadOrCreateJson(path);
        if (json["hooks"] is JsonObject hooks)
        {
            RemoveCodeIslandClaudeHooks(hooks);
            if (JsonObjectHasValues(hooks))
                json["hooks"] = hooks;
            else
                json.Remove("hooks");
        }
        else if (json["hooks"] is JsonArray legacyHooks)
        {
            RemoveMatching(legacyHooks, IsCodeIslandClaudeMatcherGroup);
            if (legacyHooks.Count == 0)
                json.Remove("hooks");
        }
        WriteJson(path, json);
        return true;
    }

    private static bool IsInstalledClaude()
    {
        var path = GetClaudeSettingsPath();
        if (!File.Exists(path)) return false;

        try
        {
            var json = ReadJson(path) as JsonObject;
            return json?["hooks"] switch
            {
                JsonObject hooks => ContainsAllRequiredCodeIslandClaudeHooks(hooks),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static JsonNode BuildClaudeHooks()
    {
        var json = new JsonObject();
        foreach (var evt in ClaudeHookEvents)
            MergeClaudeHookEvent(json, evt, "claude");
        return json;
    }

    private static void MergeClaudeHookEvent(JsonObject hooks, string eventName, string source)
    {
        var matcherGroups = hooks[eventName] as JsonArray ?? new JsonArray();
        RemoveMatching(matcherGroups, IsCodeIslandClaudeMatcherGroup);
        matcherGroups.Add(BuildClaudeMatcherGroup(source, GetClaudeHookTimeout(eventName)));
        hooks[eventName] = matcherGroups;
    }

    private static int GetClaudeHookTimeout(string eventName) =>
        eventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase) ||
        eventName.Equals("PermissionRequest", StringComparison.OrdinalIgnoreCase) ||
        eventName.Equals("Notification", StringComparison.OrdinalIgnoreCase)
            ? 86400
            : 10;

    private static JsonObject BuildClaudeMatcherGroup(string source, int timeoutSeconds) => new()
    {
        ["matcher"] = "",
        ["hooks"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "command",
                ["command"] = GetHookCommand(source),
                ["timeout"] = timeoutSeconds
            }
        }
    };

    private static bool IsCodeIslandClaudeMatcherGroup(JsonNode? node)
    {
        if (node is not JsonObject group || group["hooks"] is not JsonArray commands)
            return false;
        return commands.Any(IsCodeIslandCommandObject);
    }

    private static void RemoveCodeIslandClaudeHooks(JsonObject hooks)
    {
        foreach (var eventName in hooks.Select(static kvp => kvp.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray matcherGroups)
                continue;

            for (var groupIndex = matcherGroups.Count - 1; groupIndex >= 0; groupIndex--)
            {
                if (matcherGroups[groupIndex] is not JsonObject group || group["hooks"] is not JsonArray commands)
                    continue;

                RemoveMatching(commands, IsCodeIslandCommandObject);
                if (commands.Count == 0)
                    matcherGroups.RemoveAt(groupIndex);
            }

            if (matcherGroups.Count == 0)
                hooks.Remove(eventName);
        }
    }

    private static bool HasAnyCodeIslandClaudeHook()
    {
        var path = GetClaudeSettingsPath();
        if (!File.Exists(path)) return false;

        try
        {
            var json = ReadJson(path) as JsonObject;
            return json?["hooks"] switch
            {
                JsonObject hooks => ContainsAnyCodeIslandClaudeHook(hooks),
                JsonArray legacyHooks => legacyHooks.Any(IsCodeIslandClaudeMatcherGroup),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsAnyCodeIslandClaudeHook(JsonObject hooks)
    {
        foreach (var eventHooks in hooks.Select(static kvp => kvp.Value))
        {
            if (eventHooks is JsonArray matcherGroups && matcherGroups.Any(IsCodeIslandClaudeMatcherGroup))
                return true;
        }
        return false;
    }

    private static bool ContainsAllRequiredCodeIslandClaudeHooks(JsonObject hooks)
    {
        foreach (var eventName in ClaudeHookEvents)
        {
            if (hooks[eventName] is not JsonArray matcherGroups || !matcherGroups.Any(IsCodeIslandClaudeMatcherGroup))
                return false;
        }

        return true;
    }

    // ============================================================
    // .nested 格式 (Gemini)
    // ============================================================

    private static string GetNestedSettingsPath(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "gemini" => Path.Combine(
                UserProfileDirectory,
                ".gemini", "settings.json"),
            _ => throw new ArgumentException($"Unsupported nested source: {source}")
        };
    }

    private static bool InstallNested(string source)
    {
        var path = GetNestedSettingsPath(source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = ReadOrCreateJson(path);
        var hooks = json["hooks"] as JsonObject ?? new JsonObject();
        foreach (var evt in HookEvents)
            MergeSimpleHookEvent(hooks, evt, source);
        json["hooks"] = hooks;

        WriteJson(path, json);
        return true;
    }

    private static bool UninstallNested(string source)
    {
        var path = GetNestedSettingsPath(source);
        if (!File.Exists(path)) return true;

        var json = ReadOrCreateJson(path);
        if (json["hooks"] is JsonObject hooks)
        {
            RemoveCodeIslandSimpleHooks(hooks);
            if (JsonObjectHasValues(hooks))
                json["hooks"] = hooks;
            else
                json.Remove("hooks");
        }
        WriteJson(path, json);
        return true;
    }

    private static bool IsInstalledNested(string source)
    {
        var path = GetNestedSettingsPath(source);
        if (!File.Exists(path)) return false;

        try
        {
            var json = ReadJson(path) as JsonObject;
            return json?["hooks"] is JsonObject hooks && ContainsCodeIslandSimpleHook(hooks);
        }
        catch
        {
            return false;
        }
    }

    private static JsonNode BuildNestedHooks(string source)
    {
        var json = new JsonObject();
        foreach (var evt in HookEvents)
            MergeSimpleHookEvent(json, evt, source);
        return json;
    }

    private static void MergeSimpleHookEvent(JsonObject hooks, string eventName, string source)
    {
        var eventHooks = hooks[eventName] as JsonArray ?? new JsonArray();
        RemoveMatching(eventHooks, IsCodeIslandCommandObject);
        eventHooks.Add(new JsonObject { ["command"] = GetHookCommand(source), ["timeout"] = 10 });
        hooks[eventName] = eventHooks;
    }

    private static void RemoveCodeIslandSimpleHooks(JsonObject hooks)
    {
        foreach (var eventName in hooks.Select(static kvp => kvp.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray eventHooks)
                continue;
            RemoveMatching(eventHooks, IsCodeIslandCommandObject);
            if (eventHooks.Count == 0)
                hooks.Remove(eventName);
        }
    }

    private static bool ContainsCodeIslandSimpleHook(JsonObject hooks)
    {
        foreach (var eventHooks in hooks.Select(static kvp => kvp.Value))
        {
            if (eventHooks is JsonArray array && array.Any(IsCodeIslandCommandObject))
                return true;
        }
        return false;
    }

    // ============================================================
    // Codex CLI 格式 (hooks.json + config.toml)
    // ============================================================
    //
    // Codex 与 gemini 都用 hooks.json 嵌套结构，但 entry schema 不同：
    //   - Codex: 每个 entry 是 { "hooks": [ { "type": "command", "command", "timeout" } ] }，无 matcher。
    //   - gemini: 沿用历史扁平 entry { "command", "timeout" }（保持不变，避免回归）。
    // 此外 Codex 0.129+ 还需要在 $CODEX_HOME/config.toml 写 [features] hooks = true，否则 hook 不触发。

    /// <summary>
    /// 解析 Codex 配置根目录：读取 CODEX_HOME，trim；空白回退 ~/.codex；
    /// "~" 展开为用户目录；"~/..." 展开为用户目录 + 余下路径；否则原样使用。
    /// hooks.json 与 config.toml 共用此解析。尊重 CODEISLAND_TEST_USERPROFILE 以隔离测试。
    /// </summary>
    public static string ResolveCodexHome()
    {
        var raw = (Environment.GetEnvironmentVariable("CODEX_HOME") ?? string.Empty).Trim();
        if (raw.Length == 0)
            return Path.Combine(UserProfileDirectory, ".codex");
        if (raw == "~")
            return UserProfileDirectory;
        if (raw.StartsWith("~/", StringComparison.Ordinal) || raw.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(UserProfileDirectory, raw[2..]);
        return raw;
    }

    private static string GetCodexHooksPath() => Path.Combine(ResolveCodexHome(), "hooks.json");

    private static string GetCodexConfigTomlPath() => Path.Combine(ResolveCodexHome(), "config.toml");

    private static bool InstallCodex()
    {
        var path = GetCodexHooksPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = ReadOrCreateJson(path);
        var hooks = json["hooks"] as JsonObject ?? new JsonObject();
        foreach (var (evt, timeout) in CodexHookEvents)
            MergeCodexHookEvent(hooks, evt, timeout);
        json["hooks"] = hooks;

        WriteJson(path, json);

        // hooks.json 写完后，确保 config.toml 打开 [features] hooks = true（Codex 0.129+ 必需）。
        EnableCodexHooksConfig();
        return true;
    }

    private static bool UninstallCodex()
    {
        // 对齐参考实现：保守处理。只从 hooks.json 移除 CodeIsland 自有 entry，
        // 不动 config.toml 的 [features] hooks 开关（用户可能还有其它 Codex hook 依赖它）。
        var path = GetCodexHooksPath();
        if (!File.Exists(path)) return true;

        var json = ReadOrCreateJson(path);
        if (json["hooks"] is JsonObject hooks)
        {
            RemoveCodeIslandCodexHooks(hooks);
            if (JsonObjectHasValues(hooks))
                json["hooks"] = hooks;
            else
                json.Remove("hooks");
        }
        WriteJson(path, json);
        return true;
    }

    private static bool IsInstalledCodex()
    {
        var path = GetCodexHooksPath();
        if (!File.Exists(path)) return false;

        try
        {
            var json = ReadJson(path) as JsonObject;
            return json?["hooks"] is JsonObject hooks && ContainsCodeIslandCodexHook(hooks);
        }
        catch
        {
            return false;
        }
    }

    private static JsonNode BuildCodexHooks()
    {
        var json = new JsonObject();
        foreach (var (evt, timeout) in CodexHookEvents)
            MergeCodexHookEvent(json, evt, timeout);
        return json;
    }

    private static void MergeCodexHookEvent(JsonObject hooks, string eventName, int timeoutSeconds)
    {
        var eventEntries = hooks[eventName] as JsonArray ?? new JsonArray();
        RemoveMatching(eventEntries, IsCodeIslandCodexEntry);
        eventEntries.Add(BuildCodexEntry(timeoutSeconds));
        hooks[eventName] = eventEntries;
    }

    private static JsonObject BuildCodexEntry(int timeoutSeconds) => new()
    {
        ["hooks"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "command",
                ["command"] = GetHookCommand("codex"),
                ["commandWindows"] = GetCodexWindowsCommand(),
                ["timeout"] = timeoutSeconds
            }
        }
    };

    /// <summary>
    /// Codex on Windows runs each hook via <c>cmd.exe /C &lt;command&gt;</c>，把整条命令作为单个 Rust
    /// <c>Command::arg</c> 传入；Rust 会把内嵌的 <c>"</c> 转义成 <c>\"</c>，cmd 的 /C 规则又会剥掉首尾引号，
    /// 导致带引号的 <c>command</c>（如 "C:\...\bridge.exe" --source codex）被破坏成无效的程序 token、退出码 1。
    /// 因此 Codex 专用的 commandWindows 必须是“无引号”调用：经 Rust 包裹 + cmd /C 剥引号后仍能正确执行。
    /// 详见 .trellis/tasks/06-02-codex-cli-hook-support/research/codex-windows-command-execution.md。
    /// </summary>
    private static string GetCodexWindowsCommand()
    {
        var path = BridgeExePath;

        // 无引号路径若含空格，会在 cmd 剥引号后被切断。优先用 8.3 短路径规避；
        // 短路径不可用（返回 0/空）或仍含空格时，退回完整路径（best-effort）。
        if (path.Contains(' '))
        {
            var shortPath = TryGetShortPath(path);
            if (!string.IsNullOrEmpty(shortPath) && !shortPath.Contains(' '))
                path = shortPath;
        }

        return $"{path} --source codex";
    }

    private static string? TryGetShortPath(string longPath)
    {
        try
        {
            var buffer = new System.Text.StringBuilder(260);
            var length = GetShortPathName(longPath, buffer, (uint)buffer.Capacity);
            if (length == 0)
                return null;
            if (length > buffer.Capacity)
            {
                buffer = new System.Text.StringBuilder((int)length);
                length = GetShortPathName(longPath, buffer, (uint)buffer.Capacity);
                if (length == 0)
                    return null;
            }
            return buffer.ToString();
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, uint cchBuffer);

    private static bool IsCodeIslandCodexEntry(JsonNode? node)
    {
        if (node is not JsonObject entry || entry["hooks"] is not JsonArray commands)
            return false;
        return commands.Any(IsCodeIslandCommandObject);
    }

    private static void RemoveCodeIslandCodexHooks(JsonObject hooks)
    {
        foreach (var eventName in hooks.Select(static kvp => kvp.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray eventEntries)
                continue;

            for (var entryIndex = eventEntries.Count - 1; entryIndex >= 0; entryIndex--)
            {
                if (eventEntries[entryIndex] is not JsonObject entry || entry["hooks"] is not JsonArray commands)
                    continue;

                RemoveMatching(commands, IsCodeIslandCommandObject);
                if (commands.Count == 0)
                    eventEntries.RemoveAt(entryIndex);
            }

            if (eventEntries.Count == 0)
                hooks.Remove(eventName);
        }
    }

    private static bool ContainsCodeIslandCodexHook(JsonObject hooks)
    {
        foreach (var eventEntries in hooks.Select(static kvp => kvp.Value))
        {
            if (eventEntries is JsonArray array && array.Any(IsCodeIslandCodexEntry))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 幂等写入 $CODEX_HOME/config.toml 的 [features] hooks = true。
    /// 采用 line/regex 编辑，绝不结构化解析 + 重新序列化 TOML——
    /// 用户 config 含 Windows 路径表键、model_providers、多个 mcp_servers，结构化 round-trip 会损坏它。
    /// 规则（对齐 macOS enableCodexHooksConfig）：
    ///   - 已有非注释 hooks = true → no-op。
    ///   - 非注释 hooks = false → 原地翻转为 hooks = true。
    ///   - 旧名 codex_hooks = true|false → 替换为 hooks = true。
    ///   - 否则插入到 [features] 行之后；无 [features] 段则在文件末尾追加。
    /// 返回 false 仅表示本次未能写入（IO 失败），不抛出。
    /// </summary>
    private static bool EnableCodexHooksConfig()
    {
        var configPath = GetCodexConfigTomlPath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var contents = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;

            // 已是 true（非注释）——不要动。
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    contents, @"^\s*hooks\s*=\s*true", System.Text.RegularExpressions.RegexOptions.Multiline))
                return true;

            // 设为 false（非注释）——原地翻转为 true。
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    contents, @"^\s*hooks\s*=\s*false", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                var flipped = System.Text.RegularExpressions.Regex.Replace(
                    contents, @"^\s*hooks\s*=\s*false", "hooks = true",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
                File.WriteAllText(configPath, flipped);
                return true;
            }

            // 迁移旧版 Codex 使用的 feature 名。
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    contents, @"^\s*codex_hooks\s*=\s*(true|false)", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                var migrated = System.Text.RegularExpressions.Regex.Replace(
                    contents, @"^\s*codex_hooks\s*=\s*(true|false)", "hooks = true",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
                File.WriteAllText(configPath, migrated);
                return true;
            }

            // 不存在——插入到 [features] 段，或追加新段。
            // 保留文件原有换行风格：CRLF 文件不要被改成混合 LF/CRLF。
            var newline = contents.Contains("\r\n") ? "\r\n" : "\n";
            var lines = contents.Replace("\r\n", "\n").Split('\n').ToList();
            var featureIndex = lines.FindIndex(line => line.Trim() == "[features]");
            if (featureIndex >= 0)
            {
                lines.Insert(featureIndex + 1, "hooks = true");
            }
            else
            {
                if (lines.Count > 0 && lines[^1].Length > 0)
                    lines.Add(string.Empty);
                lines.Add("[features]");
                lines.Add("hooks = true");
            }

            File.WriteAllText(configPath, string.Join(newline, lines));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // .flat 格式 (Cursor, Trae)
    // ============================================================

    private static string GetFlatSettingsPath(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "cursor" or "cursor-cli" => Path.Combine(
                UserProfileDirectory,
                ".cursor", "hooks.json"),
            "trae" or "traecn" or "traecli" => Path.Combine(
                UserProfileDirectory,
                ".trae", "hooks.json"),
            _ => throw new ArgumentException($"Unsupported flat source: {source}")
        };
    }

    private static bool InstallFlat(string source)
    {
        var path = GetFlatSettingsPath(source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = ReadJson(path) as JsonArray ?? new JsonArray();
        RemoveMatching(json, IsCodeIslandCommandObject);
        foreach (var evt in HookEvents)
        {
            json.Add(new JsonObject
            {
                ["event"] = evt,
                ["command"] = GetHookCommand(source),
                ["timeout"] = 10
            });
        }
        WriteJson(path, json);
        return true;
    }

    private static bool UninstallFlat(string source)
    {
        var path = GetFlatSettingsPath(source);
        if (!File.Exists(path)) return true;
        var json = ReadJson(path) as JsonArray ?? new JsonArray();
        RemoveMatching(json, IsCodeIslandCommandObject);
        WriteJson(path, json);
        return true;
    }

    private static bool IsInstalledFlat(string source)
    {
        var path = GetFlatSettingsPath(source);
        if (!File.Exists(path)) return false;
        try
        {
            return ReadJson(path) is JsonArray array && array.Any(IsCodeIslandCommandObject);
        }
        catch
        {
            return false;
        }
    }

    private static JsonNode BuildFlatHooks(string source)
    {
        var command = GetHookCommand(source);
        var array = new JsonArray();
        foreach (var evt in HookEvents)
        {
            array.Add(new JsonObject
            {
                ["event"] = evt,
                ["command"] = command,
                ["timeout"] = 10
            });
        }
        return array;
    }

    // ============================================================
    // .copilot 格式 (GitHub Copilot CLI)
    // ============================================================

    private static string GetCopilotHooksDir() =>
        Path.Combine(UserProfileDirectory,
            ".copilot", "hooks");

    private static bool InstallCopilot()
    {
        var dir = GetCopilotHooksDir();
        Directory.CreateDirectory(dir);

        var hooksFile = Path.Combine(dir, "hooks.json");
        var hooksConfig = ReadOrCreateJson(hooksFile);
        hooksConfig["version"] ??= 1;

        var hooksArray = hooksConfig["hooks"] as JsonArray ?? new JsonArray();
        RemoveMatching(hooksArray, IsCodeIslandCommandObject);
        foreach (var evt in HookEvents)
        {
            hooksArray.Add(new JsonObject
            {
                ["event"] = evt,
                ["command"] = GetHookCommand("copilot"),
                ["timeout"] = 10
            });
        }
        hooksConfig["hooks"] = hooksArray;

        WriteJson(hooksFile, hooksConfig);
        return true;
    }

    private static bool UninstallCopilot()
    {
        var hooksFile = Path.Combine(GetCopilotHooksDir(), "hooks.json");
        if (!File.Exists(hooksFile)) return true;

        var hooksConfig = ReadOrCreateJson(hooksFile);
        if (hooksConfig["hooks"] is JsonArray hooksArray)
            RemoveMatching(hooksArray, IsCodeIslandCommandObject);
        WriteJson(hooksFile, hooksConfig);
        return true;
    }

    private static bool IsInstalledCopilot()
    {
        var hooksFile = Path.Combine(GetCopilotHooksDir(), "hooks.json");
        if (!File.Exists(hooksFile)) return false;
        try
        {
            var hooksConfig = ReadJson(hooksFile) as JsonObject;
            return hooksConfig?["hooks"] is JsonArray hooksArray && hooksArray.Any(IsCodeIslandCommandObject);
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // .cline 格式 (Cline VS Code)
    // ============================================================

    private static string GetClineHooksDir() =>
        Path.Combine(UserProfileDirectory,
            "Documents", "Cline", "Hooks");

    private static bool InstallCline()
    {
        var dir = GetClineHooksDir();
        Directory.CreateDirectory(dir);

        var command = GetHookCommand("cline");

        // 每事件独立可执行文件
        foreach (var evt in HookEvents)
        {
            var scriptContent = $"""
                # Auto-generated by CodeIsland
                {command}
                """;
            File.WriteAllText(Path.Combine(dir, $"{evt}.ps1"), scriptContent);
        }

        return true;
    }

    private static bool UninstallCline()
    {
        var dir = GetClineHooksDir();
        if (Directory.Exists(dir))
        {
            // 只删除 CodeIsland 生成的文件
            foreach (var evt in HookEvents)
            {
                var file = Path.Combine(dir, $"{evt}.ps1");
                if (File.Exists(file))
                {
                    var content = File.ReadAllText(file);
                    if (content.Contains("Auto-generated by CodeIsland"))
                        File.Delete(file);
                }
            }

            // 如果目录为空则删除
            if (Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }

        return true;
    }

    private static bool IsInstalledCline()
    {
        var dir = GetClineHooksDir();
        if (!Directory.Exists(dir)) return false;

        // 检查至少有一个事件脚本存在
        foreach (var evt in HookEvents)
        {
            var file = Path.Combine(dir, $"{evt}.ps1");
            if (File.Exists(file)) return true;
        }

        return false;
    }

    // ============================================================
    // JSON 辅助方法
    // ============================================================

    private static JsonObject ReadOrCreateJson(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        try
        {
            var text = File.ReadAllText(path);
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static JsonNode? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text);
    }

    private static void WriteJson(string path, JsonNode json)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, json.ToJsonString(JsonOptions));
    }
}
