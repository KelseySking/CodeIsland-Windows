using System.Text.Json.Nodes;
using CodeIsland.Core.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CodeIsland.Core.Tests;

public class ConfigInstallerTests : IDisposable
{
    private static readonly string[] ExpectedClaudeHookEvents =
    [
        "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
        "PermissionRequest", "Stop", "SubagentStart", "SubagentStop",
        "SessionStart", "SessionEnd", "Notification", "PreCompact"
    ];

    private readonly string _tempDir;
    private readonly string _fakeBridgeSourcePath;
    private readonly string? _originalUserProfileOverride;
    private readonly string? _originalCodexHome;
    private readonly string? _originalBridgeSourceOverride;

    public ConfigInstallerTests()
    {
        // 创建临时目录用于隔离测试，避免读写真实用户级 hook 配置。
        _tempDir = Path.Combine(Path.GetTempPath(), $"codeisland-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _fakeBridgeSourcePath = Path.Combine(_tempDir, "source", "CodeIsland.Bridge.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_fakeBridgeSourcePath)!);
        File.WriteAllText(_fakeBridgeSourcePath, "fake bridge binary");

        _originalUserProfileOverride = Environment.GetEnvironmentVariable("CODEISLAND_TEST_USERPROFILE");
        _originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        _originalBridgeSourceOverride = Environment.GetEnvironmentVariable("CODEISLAND_TEST_BRIDGE_SOURCE_PATH");

        Environment.SetEnvironmentVariable("CODEISLAND_TEST_USERPROFILE", _tempDir);
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(_tempDir, ".codex"));
        Environment.SetEnvironmentVariable("CODEISLAND_TEST_BRIDGE_SOURCE_PATH", _fakeBridgeSourcePath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEISLAND_TEST_USERPROFILE", _originalUserProfileOverride);
        Environment.SetEnvironmentVariable("CODEX_HOME", _originalCodexHome);
        Environment.SetEnvironmentVariable("CODEISLAND_TEST_BRIDGE_SOURCE_PATH", _originalBridgeSourceOverride);

        // 清理临时目录
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private string GetClaudeSettingsPath() => Path.Combine(_tempDir, ".claude", "settings.json");

    private string GetRuntimeHookScriptPath() => Path.Combine(_tempDir, ".codeisland", "codeisland-hook.ps1");

    private string GetRuntimeBridgePath() => Path.Combine(_tempDir, ".codeisland", "codeisland-bridge.exe");

    private static int GetClaudeHookTimeout(JsonObject hooks, string eventName)
    {
        var matcherGroups = Assert.IsType<JsonArray>(hooks[eventName]);
        var matcherGroup = Assert.IsType<JsonObject>(matcherGroups[0]);
        var hookCommands = Assert.IsType<JsonArray>(matcherGroup["hooks"]);
        var hookCommand = Assert.IsType<JsonObject>(hookCommands[0]);
        return hookCommand["timeout"]?.GetValue<int>() ?? throw new InvalidOperationException($"Missing timeout for {eventName}");
    }

    // ============================================================
    // 支持的 Source 列表测试
    // ============================================================

    [Fact]
    public void SupportedSources_ContainsExpectedSources()
    {
        var sources = ConfigInstaller.SupportedSources;
        Assert.Contains("claude", sources);
        Assert.Contains("codex", sources);
        Assert.Contains("gemini", sources);
        Assert.Contains("cursor", sources);
        Assert.Contains("copilot", sources);
        Assert.Contains("cline", sources);
    }

    // ============================================================
    // Unsupported source 测试
    // ============================================================

    [Fact]
    public void Install_UnsupportedSource_ReturnsFalse()
    {
        var result = ConfigInstaller.Install("unsupported-tool");
        Assert.False(result);
    }

    [Fact]
    public void Uninstall_UnsupportedSource_ReturnsFalse()
    {
        var result = ConfigInstaller.Uninstall("unsupported-tool");
        Assert.False(result);
    }

    [Fact]
    public void IsInstalled_UnsupportedSource_ReturnsFalse()
    {
        var result = ConfigInstaller.IsInstalled("unsupported-tool");
        Assert.False(result);
    }

    // ============================================================
    // Claude 格式测试 (.claude)
    // ============================================================

    [Fact]
    public void Install_Claude_CreatesHookScript()
    {
        // 安装会创建 PowerShell 脚本
        var result = ConfigInstaller.Install("claude");
        Assert.True(result);

        // 验证 hook 脚本存在于隔离测试目录
        var hookScript = GetRuntimeHookScriptPath();
        var bridgePath = GetRuntimeBridgePath();
        Assert.True(File.Exists(hookScript));
        Assert.True(File.Exists(bridgePath));

        // 验证脚本内容
        var content = File.ReadAllText(hookScript);
        Assert.Contains(bridgePath, content);
        Assert.Contains("codeisland-bridge.exe", content);
        Assert.Contains("$input", content);
    }

    [Fact]
    public void Install_Claude_WritesObjectSchemaHooks()
    {
        var settingsPath = GetClaudeSettingsPath();

        var result = ConfigInstaller.Install("claude");
        Assert.True(result);

        Assert.True(File.Exists(settingsPath));

        var json = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(json);

        var hooks = Assert.IsType<JsonObject>(json!["hooks"]);
        Assert.Equal(ExpectedClaudeHookEvents.OrderBy(static evt => evt), hooks.Select(static kvp => kvp.Key).OrderBy(static evt => evt));

        foreach (var eventName in ExpectedClaudeHookEvents)
        {
            var matcherGroups = Assert.IsType<JsonArray>(hooks[eventName]);
            Assert.Single(matcherGroups);

            var matcherGroup = Assert.IsType<JsonObject>(matcherGroups[0]);
            Assert.Equal("", matcherGroup["matcher"]?.GetValue<string>());

            var hookCommands = Assert.IsType<JsonArray>(matcherGroup["hooks"]);
            Assert.Single(hookCommands);

            var hookCommand = Assert.IsType<JsonObject>(hookCommands[0]);
            Assert.Equal("command", hookCommand["type"]?.GetValue<string>());
            var expectedTimeout = eventName is "PreToolUse" or "PermissionRequest" or "Notification" ? 86400 : 10;
            Assert.Equal(expectedTimeout, hookCommand["timeout"]?.GetValue<int>());
            var command = hookCommand["command"]?.GetValue<string>();
            Assert.Contains("codeisland-bridge.exe", command);
            Assert.Contains("--source claude", command);
        }
    }

    [Fact]
    public void Install_Claude_WritesLongTimeoutForBlockingApprovalAndQuestionHooks()
    {
        var settingsPath = GetClaudeSettingsPath();

        var result = ConfigInstaller.Install("claude");
        Assert.True(result);

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(settingsPath)));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);

        Assert.Equal(86400, GetClaudeHookTimeout(hooks, "PreToolUse"));
        Assert.Equal(86400, GetClaudeHookTimeout(hooks, "PermissionRequest"));
        Assert.Equal(86400, GetClaudeHookTimeout(hooks, "Notification"));
        Assert.Equal(10, GetClaudeHookTimeout(hooks, "PostToolUse"));
        Assert.Equal(10, GetClaudeHookTimeout(hooks, "Stop"));
    }

    [Fact]
    public void Install_Claude_MigratesArrayHooksAndPreservesUnrelatedSettings()
    {
        var settingsPath = GetClaudeSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """
            {
              "env": {
                "ANTHROPIC_AUTH_TOKEN": "preserve-token-value",
                "ANTHROPIC_BASE_URL": "https://example.invalid"
              },
              "permissions": {
                "allow": ["Bash(git status)"]
              },
              "hooks": [
                {
                  "matcher": "",
                  "hooks": [
                    {
                      "type": "command",
                      "command": "old-command",
                      "timeout": 10
                    }
                  ]
                }
              ]
            }
            """);

        var result = ConfigInstaller.Install("claude");
        Assert.True(result);

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(settingsPath)));
        Assert.IsType<JsonObject>(json["hooks"]);

        var env = Assert.IsType<JsonObject>(json["env"]);
        Assert.Equal("preserve-token-value", env["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
        Assert.Equal("https://example.invalid", env["ANTHROPIC_BASE_URL"]?.GetValue<string>());

        var permissions = Assert.IsType<JsonObject>(json["permissions"]);
        var allow = Assert.IsType<JsonArray>(permissions["allow"]);
        Assert.Equal("Bash(git status)", allow[0]?.GetValue<string>());
    }

    [Fact]
    public void Install_Claude_PreservesExistingUserHooks()
    {
        var settingsPath = GetClaudeSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      { "type": "command", "command": "user-command", "timeout": 20 }
                    ]
                  }
                ]
              }
            }
            """);

        Assert.True(ConfigInstaller.Install("claude"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(settingsPath)));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);
        var preToolUse = Assert.IsType<JsonArray>(hooks["PreToolUse"]);

        Assert.Contains(preToolUse, node =>
            node?["matcher"]?.GetValue<string>() == "Bash" &&
            node?["hooks"] is JsonArray commands &&
            commands.Any(command => command?["command"]?.GetValue<string>() == "user-command"));
        Assert.Contains(preToolUse, node =>
            node?["hooks"] is JsonArray commands &&
            commands.Any(command => command?["command"]?.GetValue<string>().Contains("codeisland-bridge.exe") == true));
    }

    [Fact]
    public void Uninstall_Claude_RemovesOnlyCodeIslandHooks()
    {
        var settingsPath = GetClaudeSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      { "type": "command", "command": "user-command", "timeout": 20 },
                      { "type": "command", "command": "powershell -NoProfile -File \"C:\\Users\\tester\\.codeisland\\codeisland-hook.ps1\"", "timeout": 10 }
                    ]
                  }
                ]
              }
            }
            """);

        Assert.True(ConfigInstaller.Uninstall("claude"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(settingsPath)));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);
        var preToolUse = Assert.IsType<JsonArray>(hooks["PreToolUse"]);
        var group = Assert.IsType<JsonObject>(preToolUse[0]);
        var commands = Assert.IsType<JsonArray>(group["hooks"]);

        Assert.Single(commands);
        Assert.Equal("user-command", commands[0]?["command"]?.GetValue<string>());
        Assert.False(ConfigInstaller.IsInstalled("claude"));
    }

    [Fact]
    public void IsInstalled_Claude_WhenNotInstalled_ReturnsFalse()
    {
        // 隔离测试目录中没有残留配置时返回 false
        var result = ConfigInstaller.IsInstalled("claude");
        Assert.False(result);
    }

    [Fact]
    public void IsInstalled_Claude_WhenBridgeMissing_ReturnsFalse()
    {
        Assert.True(ConfigInstaller.Install("claude"));
        File.Delete(GetRuntimeBridgePath());

        Assert.False(ConfigInstaller.IsInstalled("claude"));
    }

    [Fact]
    public void IsInstalled_Claude_WhenMissingRequiredEvents_ReturnsFalse()
    {
        Assert.True(ConfigInstaller.Install("claude"));
        var settingsPath = GetClaudeSettingsPath();
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(settingsPath)));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);
        hooks.Remove("Stop");
        hooks.Remove("SessionEnd");
        File.WriteAllText(settingsPath, json.ToJsonString());

        Assert.False(ConfigInstaller.IsInstalled("claude"));
    }

    [Fact]
    public void RepairInstalledHookConfigurations_UpgradesPartialClaudeHooksNonDestructively()
    {
        var settingsPath = GetClaudeSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """
            {
              "env": {
                "ANTHROPIC_AUTH_TOKEN": "preserve-token-value"
              },
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      { "type": "command", "command": "user-command", "timeout": 20 }
                    ]
                  },
                  {
                    "matcher": "",
                    "hooks": [
                      { "type": "command", "command": "powershell -NoProfile -File \"C:\\Users\\tester\\.codeisland\\codeisland-hook.ps1\" --source claude", "timeout": 10 }
                    ]
                  }
                ]
              }
            }
            """);

        Assert.True(ConfigInstaller.RepairInstalledHookConfigurations());

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(settingsPath)));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);
        Assert.Equal(ExpectedClaudeHookEvents.OrderBy(static evt => evt), hooks.Select(static kvp => kvp.Key).OrderBy(static evt => evt));
        Assert.True(ConfigInstaller.IsInstalled("claude"));

        var preToolUse = Assert.IsType<JsonArray>(hooks["PreToolUse"]);
        Assert.Contains(preToolUse, node =>
            node?["matcher"]?.GetValue<string>() == "Bash" &&
            node?["hooks"] is JsonArray commands &&
            commands.Any(command => command?["command"]?.GetValue<string>() == "user-command"));
        Assert.Equal("preserve-token-value", json["env"]?["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
    }

    [Fact]
    public void RepairInstalledHookConfigurations_WhenNoCodeIslandHook_DoesNotCreateClaudeSettingsButRepairsRuntimeAssets()
    {
        Assert.False(File.Exists(GetClaudeSettingsPath()));

        Assert.True(ConfigInstaller.RepairInstalledHookConfigurations());

        Assert.False(File.Exists(GetClaudeSettingsPath()));
        Assert.True(File.Exists(GetRuntimeBridgePath()));
        Assert.True(File.Exists(GetRuntimeHookScriptPath()));
        Assert.False(ConfigInstaller.IsInstalled("claude"));
    }

    [Fact]
    public void RepairInstalledHookConfigurations_PreservesUserOnlyClaudeHooksWithoutAddingCodeIslandHooks()
    {
        var settingsPath = GetClaudeSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """
            {
              "env": {
                "ANTHROPIC_AUTH_TOKEN": "preserve-token-value"
              },
              "permissions": {
                "allow": ["Bash(git status)"]
              },
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      { "type": "command", "command": "user-command", "timeout": 20 }
                    ]
                  }
                ]
              }
            }
            """);

        Assert.True(ConfigInstaller.RepairInstalledHookConfigurations());

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(settingsPath)));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);
        var preToolUse = Assert.IsType<JsonArray>(hooks["PreToolUse"]);
        Assert.Single(preToolUse);
        Assert.False(ConfigInstaller.IsInstalled("claude"));
        Assert.Equal("preserve-token-value", json["env"]?["ANTHROPIC_AUTH_TOKEN"]?.GetValue<string>());
        Assert.Equal("Bash(git status)", json["permissions"]?["allow"]?[0]?.GetValue<string>());
        Assert.True(File.Exists(GetRuntimeBridgePath()));
        Assert.True(File.Exists(GetRuntimeHookScriptPath()));
    }

    [Fact]
    public void RepairRuntimeAssets_WhenBridgeMissing_RestoresBridgeAndHookScript()
    {
        Assert.True(ConfigInstaller.Install("claude"));
        File.Delete(GetRuntimeBridgePath());
        File.Delete(GetRuntimeHookScriptPath());

        Assert.True(ConfigInstaller.RepairRuntimeAssets());

        Assert.True(File.Exists(GetRuntimeBridgePath()));
        Assert.True(File.Exists(GetRuntimeHookScriptPath()));
        Assert.Contains(GetRuntimeBridgePath(), File.ReadAllText(GetRuntimeHookScriptPath()));
        Assert.True(ConfigInstaller.IsInstalled("claude"));
    }

    [Fact]
    public void Install_Claude_WhenBridgeSourceMissing_ReturnsFalseAndStatusIsFalse()
    {
        Environment.SetEnvironmentVariable("CODEISLAND_TEST_BRIDGE_SOURCE_PATH", Path.Combine(_tempDir, "missing", "CodeIsland.Bridge.exe"));

        Assert.False(ConfigInstaller.Install("claude"));
        Assert.False(File.Exists(GetRuntimeBridgePath()));
        Assert.False(ConfigInstaller.IsInstalled("claude"));
    }

    // ============================================================
    // Nested 格式测试 (.nested - Codex, Gemini)
    // ============================================================

    [Fact]
    public void Install_Codex_CreatesHooksJson()
    {
        var result = ConfigInstaller.Install("codex");
        Assert.True(result);

        // 验证 hooks.json 被创建
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(_tempDir, ".codex");
        var hooksPath = Path.Combine(codexHome, "hooks.json");

        // 文件应该存在
        Assert.True(File.Exists(hooksPath), $"Expected hooks.json at {hooksPath}");

        // 验证 JSON 包含 hooks
        var content = File.ReadAllText(hooksPath);
        Assert.Contains("PreToolUse", content);
        Assert.Contains("PostToolUse", content);
        Assert.Contains("codeisland-bridge.exe", content);

        // 清理
        File.Delete(hooksPath);
    }

    [Fact]
    public void Install_Gemini_ModifiesSettingsJson()
    {
        var result = ConfigInstaller.Install("gemini");
        Assert.True(result);

        var settingsPath = Path.Combine(
            _tempDir,
            ".gemini", "settings.json");

        Assert.True(File.Exists(settingsPath));

        var content = File.ReadAllText(settingsPath);
        Assert.Contains("hooks", content);
        Assert.Contains("codeisland-bridge.exe", content);

        // 清理
        File.Delete(settingsPath);
        var dir = Path.GetDirectoryName(settingsPath);
        if (dir != null && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
            Directory.Delete(dir);
    }

    // ============================================================
    // Codex 专用注入路径测试 (hooks.json + config.toml)
    // ============================================================

    private static readonly (string Event, int Timeout)[] ExpectedCodexHookEvents =
    [
        ("SessionStart", 5),
        ("SessionEnd", 5),
        ("UserPromptSubmit", 5),
        ("PreToolUse", 86400),
        ("PostToolUse", 5),
        ("PermissionRequest", 86400),
        ("Stop", 5)
    ];

    private string GetCodexHomeDir() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(_tempDir, ".codex");

    private string GetCodexHooksPath() => Path.Combine(GetCodexHomeDir(), "hooks.json");

    private string GetCodexConfigTomlPath() => Path.Combine(GetCodexHomeDir(), "config.toml");

    [Fact]
    public void Install_Codex_WritesNestedSchemaWithTypeAndNoMatcher()
    {
        Assert.True(ConfigInstaller.Install("codex"));

        var hooksPath = GetCodexHooksPath();
        Assert.True(File.Exists(hooksPath), $"Expected hooks.json at {hooksPath}");

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(hooksPath)));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);

        // 恰好 7 个事件，无多余。
        Assert.Equal(
            ExpectedCodexHookEvents.Select(static e => e.Event).OrderBy(static e => e),
            hooks.Select(static kvp => kvp.Key).OrderBy(static e => e));

        foreach (var (eventName, expectedTimeout) in ExpectedCodexHookEvents)
        {
            var eventEntries = Assert.IsType<JsonArray>(hooks[eventName]);
            Assert.Single(eventEntries);

            var entry = Assert.IsType<JsonObject>(eventEntries[0]);
            // 嵌套 hooks 包裹层，无 matcher。
            Assert.Null(entry["matcher"]);

            var commands = Assert.IsType<JsonArray>(entry["hooks"]);
            Assert.Single(commands);

            var command = Assert.IsType<JsonObject>(commands[0]);
            Assert.Equal("command", command["type"]?.GetValue<string>());
            Assert.Equal(expectedTimeout, command["timeout"]?.GetValue<int>());
            var commandText = command["command"]?.GetValue<string>();
            Assert.Contains("codeisland-bridge.exe", commandText);
            Assert.Contains("--source codex", commandText);
        }
    }

    [Fact]
    public void Install_Codex_DoesNotIncludeNotificationEvent()
    {
        Assert.True(ConfigInstaller.Install("codex"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(GetCodexHooksPath())));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);

        Assert.False(hooks.ContainsKey("Notification"));
        Assert.True(hooks.ContainsKey("PermissionRequest"));
    }

    [Fact]
    public void Install_Codex_WritesUnquotedCommandWindowsForEveryEvent()
    {
        // Codex 在 Windows 用 cmd.exe /C 执行 hook，带引号的 command 会被破坏（退出码 1）。
        // 因此每个事件必须额外携带“无引号”的 commandWindows。
        Assert.True(ConfigInstaller.Install("codex"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(GetCodexHooksPath())));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);

        foreach (var (eventName, _) in ExpectedCodexHookEvents)
        {
            var eventEntries = Assert.IsType<JsonArray>(hooks[eventName]);
            var entry = Assert.IsType<JsonObject>(eventEntries[0]);
            var command = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(entry["hooks"])[0]);

            // commandWindows：无引号、无内嵌 "、指向 bridge、以 --source codex 结尾。
            var commandWindows = command["commandWindows"]?.GetValue<string>();
            Assert.NotNull(commandWindows);
            Assert.False(commandWindows!.StartsWith('"'), $"commandWindows for {eventName} must not start with a quote: {commandWindows}");
            Assert.DoesNotContain("\"", commandWindows);
            Assert.Contains("codeisland-bridge.exe", commandWindows);
            Assert.EndsWith("--source codex", commandWindows);

            // command（非 Windows 回退）保持原状：仍指向 bridge 且带 --source codex。
            var commandText = command["command"]?.GetValue<string>();
            Assert.NotNull(commandText);
            Assert.Contains("codeisland-bridge.exe", commandText);
            Assert.Contains("--source codex", commandText);
        }
    }

    [Fact]
    public void Install_Codex_ReinstallKeepsSingleCommandWindowsPerEvent()
    {
        Assert.True(ConfigInstaller.Install("codex"));
        Assert.True(ConfigInstaller.Install("codex"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(GetCodexHooksPath())));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);

        foreach (var (eventName, _) in ExpectedCodexHookEvents)
        {
            var eventEntries = Assert.IsType<JsonArray>(hooks[eventName]);
            // 幂等：重装后每个事件仍只有一个 CodeIsland entry。
            Assert.Single(eventEntries);

            var entry = Assert.IsType<JsonObject>(eventEntries[0]);
            var command = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(entry["hooks"])[0]);
            var commandWindows = command["commandWindows"]?.GetValue<string>();
            Assert.NotNull(commandWindows);
            Assert.False(commandWindows!.StartsWith('"'));
            Assert.EndsWith("--source codex", commandWindows);
        }
    }

    [Fact]
    public void Install_Codex_WritesFeaturesHooksTrueInConfigToml()
    {
        Assert.True(ConfigInstaller.Install("codex"));

        var configPath = GetCodexConfigTomlPath();
        Assert.True(File.Exists(configPath));

        var content = File.ReadAllText(configPath);
        Assert.Contains("[features]", content);
        Assert.Contains("hooks = true", content);
        Assert.DoesNotContain("codex_hooks", content);
    }

    [Fact]
    public void Install_Codex_PreservesExistingConfigTomlFields()
    {
        var configPath = GetCodexConfigTomlPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            model_provider = "custom"

            [model_providers.custom]
            name = "Custom"
            base_url = "https://example.invalid"

            [mcp_servers.fs]
            command = "npx"

            [projects.'d:\work\demo']
            trust_level = "trusted"

            [hooks.state.'d:\work\demo\.codex\hooks.json:user_prompt_submit:0:0']
            trusted_hash = "sha256:abc123"
            """);

        Assert.True(ConfigInstaller.Install("codex"));

        var content = File.ReadAllText(configPath);
        // 所有原字段无损。
        Assert.Contains("model_provider = \"custom\"", content);
        Assert.Contains("[model_providers.custom]", content);
        Assert.Contains("[mcp_servers.fs]", content);
        Assert.Contains("[projects.'d:\\work\\demo']", content);
        Assert.Contains("trusted_hash = \"sha256:abc123\"", content);
        // 新增 feature 开关。
        Assert.Contains("[features]", content);
        Assert.Contains("hooks = true", content);
    }

    [Fact]
    public void EnableCodexHooks_IsIdempotentWhenAlreadyTrue()
    {
        var configPath = GetCodexConfigTomlPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            [features]
            hooks = true
            other = "keep"
            """);

        Assert.True(ConfigInstaller.Install("codex"));

        var content = File.ReadAllText(configPath);
        // 不应重复插入。
        var occurrences = content.Split("hooks = true").Length - 1;
        Assert.Equal(1, occurrences);
        Assert.Contains("other = \"keep\"", content);
    }

    [Fact]
    public void EnableCodexHooks_FlipsFalseToTrue()
    {
        var configPath = GetCodexConfigTomlPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            [features]
            hooks = false
            """);

        Assert.True(ConfigInstaller.Install("codex"));

        var content = File.ReadAllText(configPath);
        Assert.Contains("hooks = true", content);
        Assert.DoesNotContain("hooks = false", content);
    }

    [Fact]
    public void EnableCodexHooks_MigratesLegacyCodexHooksName()
    {
        var configPath = GetCodexConfigTomlPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            [features]
            codex_hooks = true
            """);

        Assert.True(ConfigInstaller.Install("codex"));

        var content = File.ReadAllText(configPath);
        Assert.Contains("hooks = true", content);
        Assert.DoesNotContain("codex_hooks", content);
    }

    [Fact]
    public void EnableCodexHooks_AppendsFeaturesSectionWhenAbsent()
    {
        var configPath = GetCodexConfigTomlPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "model_provider = \"custom\"\n");

        Assert.True(ConfigInstaller.Install("codex"));

        var content = File.ReadAllText(configPath);
        Assert.Contains("model_provider = \"custom\"", content);
        Assert.Contains("[features]", content);
        Assert.Contains("hooks = true", content);
    }

    [Fact]
    public void EnableCodexHooks_PreservesCrlfNewlineStyleWhenAppending()
    {
        var configPath = GetCodexConfigTomlPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        // CRLF 文件（无 [features] 段，走追加分支）。
        File.WriteAllText(configPath, "model_provider = \"custom\"\r\n[windows]\r\nsandbox = \"elevated\"\r\n");

        Assert.True(ConfigInstaller.Install("codex"));

        var content = File.ReadAllText(configPath);
        Assert.Contains("[features]", content);
        Assert.Contains("hooks = true", content);
        // 原有字段无损。
        Assert.Contains("sandbox = \"elevated\"", content);
        // 不得引入混合换行：CRLF 文件保持纯 CRLF（无裸 \n）。
        Assert.DoesNotContain("\n", content.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void EnableCodexHooks_IgnoresCommentedHooksAndInsertsRealOne()
    {
        var configPath = GetCodexConfigTomlPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            [features]
            # hooks = false
            """);

        Assert.True(ConfigInstaller.Install("codex"));

        var content = File.ReadAllText(configPath);
        // 注释行保留，且新增一行非注释 hooks = true。
        Assert.Contains("# hooks = false", content);
        Assert.Matches(@"(?m)^\s*hooks\s*=\s*true", content);
    }

    [Fact]
    public void ResolveCodexHome_ExpandsTildePrefix()
    {
        // CODEX_HOME = "~/custom-codex" 应展开到隔离测试用户目录下。
        Environment.SetEnvironmentVariable("CODEX_HOME", "~/custom-codex");

        Assert.True(ConfigInstaller.Install("codex"));

        var expectedHooks = Path.Combine(_tempDir, "custom-codex", "hooks.json");
        Assert.True(File.Exists(expectedHooks), $"Expected hooks.json at {expectedHooks}");
        Assert.True(File.Exists(Path.Combine(_tempDir, "custom-codex", "config.toml")));
    }

    [Fact]
    public void ResolveCodexHome_BlankFallsBackToDotCodex()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", "   ");

        Assert.True(ConfigInstaller.Install("codex"));

        var expectedHooks = Path.Combine(_tempDir, ".codex", "hooks.json");
        Assert.True(File.Exists(expectedHooks), $"Expected hooks.json at {expectedHooks}");
    }

    [Fact]
    public void Install_Codex_ReinstallIsIdempotent()
    {
        Assert.True(ConfigInstaller.Install("codex"));
        Assert.True(ConfigInstaller.Install("codex"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(GetCodexHooksPath())));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);

        foreach (var (eventName, _) in ExpectedCodexHookEvents)
        {
            var eventEntries = Assert.IsType<JsonArray>(hooks[eventName]);
            // 重装后每个事件仍只有一个 CodeIsland entry。
            Assert.Single(eventEntries);
        }

        Assert.True(ConfigInstaller.IsInstalled("codex"));
    }

    [Fact]
    public void Install_Codex_PreservesExistingUserHooks()
    {
        var hooksPath = GetCodexHooksPath();
        Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
        File.WriteAllText(hooksPath, """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "hooks": [
                      { "type": "command", "command": "user-command", "timeout": 30 }
                    ]
                  }
                ]
              }
            }
            """);

        Assert.True(ConfigInstaller.Install("codex"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(hooksPath)));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);
        var preToolUse = Assert.IsType<JsonArray>(hooks["PreToolUse"]);

        // 用户 entry 与 CodeIsland entry 共存。
        Assert.Contains(preToolUse, node =>
            node?["hooks"] is JsonArray commands &&
            commands.Any(c => c?["command"]?.GetValue<string>() == "user-command"));
        Assert.Contains(preToolUse, node =>
            node?["hooks"] is JsonArray commands &&
            commands.Any(c => c?["command"]?.GetValue<string>()?.Contains("codeisland-bridge.exe") == true));
    }

    [Fact]
    public void Uninstall_Codex_RemovesOnlyCodeIslandEntry()
    {
        var hooksPath = GetCodexHooksPath();
        Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
        File.WriteAllText(hooksPath, """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "hooks": [
                      { "type": "command", "command": "user-command", "timeout": 30 }
                    ]
                  }
                ]
              }
            }
            """);

        // 先安装让 CodeIsland entry 加入，再卸载。
        Assert.True(ConfigInstaller.Install("codex"));
        Assert.True(ConfigInstaller.Uninstall("codex"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(hooksPath)));
        var hooks = Assert.IsType<JsonObject>(json["hooks"]);
        var preToolUse = Assert.IsType<JsonArray>(hooks["PreToolUse"]);

        // 只剩用户 entry。
        Assert.Single(preToolUse);
        var entry = Assert.IsType<JsonObject>(preToolUse[0]);
        var commands = Assert.IsType<JsonArray>(entry["hooks"]);
        Assert.Single(commands);
        Assert.Equal("user-command", commands[0]?["command"]?.GetValue<string>());
        Assert.False(ConfigInstaller.IsInstalled("codex"));
    }

    [Fact]
    public void Uninstall_Codex_LeavesConfigTomlFeatureFlagUntouched()
    {
        Assert.True(ConfigInstaller.Install("codex"));
        var configPath = GetCodexConfigTomlPath();
        Assert.Contains("hooks = true", File.ReadAllText(configPath));

        Assert.True(ConfigInstaller.Uninstall("codex"));

        // 保守：卸载不动 config.toml 的 feature 开关。
        Assert.Contains("hooks = true", File.ReadAllText(configPath));
    }

    [Fact]
    public void Install_ThenUninstall_Codex_IsInstalledReturnsFalse()
    {
        Assert.True(ConfigInstaller.Install("codex"));
        Assert.True(ConfigInstaller.IsInstalled("codex"));

        Assert.True(ConfigInstaller.Uninstall("codex"));
        Assert.False(ConfigInstaller.IsInstalled("codex"));
    }

    // ============================================================
    // Flat 格式测试 (.flat - Cursor, Trae)
    // ============================================================

    [Fact]
    public void Install_Cursor_CreatesHooksJson()
    {
        var result = ConfigInstaller.Install("cursor");
        Assert.True(result);

        var hooksPath = Path.Combine(
            _tempDir,
            ".cursor", "hooks.json");

        Assert.True(File.Exists(hooksPath), $"Expected hooks.json at {hooksPath}");

        var content = File.ReadAllText(hooksPath);
        // 扁平格式应该是数组
        Assert.Contains("[", content);
        Assert.Contains("event", content);
        Assert.Contains("command", content);
        Assert.Contains("codeisland-bridge.exe", content);

        // 清理
        File.Delete(hooksPath);
        var dir = Path.GetDirectoryName(hooksPath);
        if (dir != null && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
            Directory.Delete(dir);
    }

    // ============================================================
    // Copilot 格式测试 (.copilot)
    // ============================================================

    [Fact]
    public void Install_Copilot_CreatesHooksDirectory()
    {
        var result = ConfigInstaller.Install("copilot");
        Assert.True(result);

        var hooksDir = Path.Combine(
            _tempDir,
            ".copilot", "hooks");
        var hooksFile = Path.Combine(hooksDir, "hooks.json");

        Assert.True(Directory.Exists(hooksDir));
        Assert.True(File.Exists(hooksFile));

        var content = File.ReadAllText(hooksFile);
        Assert.Contains("version", content);
        Assert.Contains("hooks", content);
        Assert.Contains("codeisland-bridge.exe", content);

        // 清理
        Directory.Delete(hooksDir, true);
        var parentDir = Path.GetDirectoryName(hooksDir);
        if (parentDir != null && Directory.Exists(parentDir) && Directory.GetFileSystemEntries(parentDir).Length == 0)
            Directory.Delete(parentDir);
    }

    // ============================================================
    // Cline 格式测试 (.cline)
    // ============================================================

    [Fact]
    public void Install_Cline_CreatesPerEventScripts()
    {
        var result = ConfigInstaller.Install("cline");
        Assert.True(result);

        var hooksDir = Path.Combine(
            _tempDir,
            "Documents", "Cline", "Hooks");

        Assert.True(Directory.Exists(hooksDir));

        // 验证至少一个事件脚本存在
        var preToolUseScript = Path.Combine(hooksDir, "PreToolUse.ps1");
        Assert.True(File.Exists(preToolUseScript), $"Expected PreToolUse.ps1 at {preToolUseScript}");

        var content = File.ReadAllText(preToolUseScript);
        Assert.Contains("Auto-generated by CodeIsland", content);
        Assert.Contains("codeisland-bridge.exe", content);

        // 清理
        Directory.Delete(hooksDir, true);
    }

    // ============================================================
    // GetInstallStatuses 测试
    // ============================================================

    [Fact]
    public void GetInstallStatuses_ReturnsAllSources()
    {
        var statuses = ConfigInstaller.GetInstallStatuses();
        Assert.NotEmpty(statuses);
        Assert.Contains("claude", statuses.Keys);
        Assert.Contains("codex", statuses.Keys);
        Assert.Contains("gemini", statuses.Keys);
        Assert.Contains("cursor", statuses.Keys);
        Assert.Contains("copilot", statuses.Keys);
        Assert.Contains("cline", statuses.Keys);
    }

    [Fact]
    public void GetInstallStatuses_AllValuesAreBool()
    {
        var statuses = ConfigInstaller.GetInstallStatuses();
        foreach (var kvp in statuses)
        {
            // 值应该是 bool 类型（true 或 false）
            Assert.IsType<bool>(kvp.Value);
        }
    }

    // ============================================================
    // 卸载测试
    // ============================================================

    [Fact]
    public void Uninstall_Claude_WhenNotInstalled_ReturnsTrue()
    {
        // 卸载不存在的配置应该成功（幂等操作）
        var result = ConfigInstaller.Uninstall("claude");
        Assert.True(result);
    }

    [Fact]
    public void Uninstall_Cursor_WhenNotInstalled_ReturnsTrue()
    {
        var result = ConfigInstaller.Uninstall("cursor");
        Assert.True(result);
    }

    [Fact]
    public void Uninstall_Codex_WhenNotInstalled_ReturnsTrue()
    {
        var result = ConfigInstaller.Uninstall("codex");
        Assert.True(result);
    }

    [Fact]
    public void Uninstall_Gemini_WhenNotInstalled_ReturnsTrue()
    {
        var result = ConfigInstaller.Uninstall("gemini");
        Assert.True(result);
    }

    [Fact]
    public void Uninstall_Copilot_WhenNotInstalled_ReturnsTrue()
    {
        var result = ConfigInstaller.Uninstall("copilot");
        Assert.True(result);
    }

    [Fact]
    public void Uninstall_Cline_WhenNotInstalled_ReturnsTrue()
    {
        var result = ConfigInstaller.Uninstall("cline");
        Assert.True(result);
    }

    // ============================================================
    // 安装后检测测试
    // ============================================================

    [Fact]
    public void Install_ThenUninstall_Cursor_IsInstalledReturnsFalse()
    {
        // 安装
        ConfigInstaller.Install("cursor");

        // 验证已安装
        var installed = ConfigInstaller.IsInstalled("cursor");
        Assert.True(installed);

        // 卸载
        ConfigInstaller.Uninstall("cursor");

        // 验证已卸载
        installed = ConfigInstaller.IsInstalled("cursor");
        Assert.False(installed);
    }

    [Fact]
    public void Install_ThenUninstall_Copilot_IsInstalledReturnsFalse()
    {
        // 安装
        ConfigInstaller.Install("copilot");

        // 验证已安装
        var installed = ConfigInstaller.IsInstalled("copilot");
        Assert.True(installed);

        // 卸载
        ConfigInstaller.Uninstall("copilot");

        // 验证已卸载
        installed = ConfigInstaller.IsInstalled("copilot");
        Assert.False(installed);
    }

    [Fact]
    public void Install_ThenUninstall_Cline_IsInstalledReturnsFalse()
    {
        // 安装
        ConfigInstaller.Install("cline");

        // 验证已安装
        var installed = ConfigInstaller.IsInstalled("cline");
        Assert.True(installed);

        // 卸载
        ConfigInstaller.Uninstall("cline");

        // 验证已卸载
        installed = ConfigInstaller.IsInstalled("cline");
        Assert.False(installed);
    }

    // ============================================================
    // 重新安装测试
    // ============================================================

    [Fact]
    public void Install_Reinstall_Cursor_Succeeds()
    {
        // 首次安装
        var first = ConfigInstaller.Install("cursor");
        Assert.True(first);

        // 重新安装（应该成功，幂等操作）
        var second = ConfigInstaller.Install("cursor");
        Assert.True(second);

        // 验证仍然已安装
        Assert.True(ConfigInstaller.IsInstalled("cursor"));

        // 清理
        ConfigInstaller.Uninstall("cursor");
    }

    [Fact]
    public void Install_Reinstall_Copilot_Succeeds()
    {
        // 首次安装
        var first = ConfigInstaller.Install("copilot");
        Assert.True(first);

        // 重新安装
        var second = ConfigInstaller.Install("copilot");
        Assert.True(second);

        // 验证仍然已安装
        Assert.True(ConfigInstaller.IsInstalled("copilot"));

        // 清理
        ConfigInstaller.Uninstall("copilot");
    }
}
