namespace CodeIsland.Core.Sources;

public static class CodeIslandSourceAdapterRegistry
{
    private static readonly ICodeIslandSourceAdapter UnknownAdapter =
        new BuiltInSourceAdapter(
            "unknown",
            "未知工具",
            "unknown",
            CodeIslandPermissionResponseStyle.ClaudeStyle);

    private static readonly Dictionary<string, ICodeIslandSourceAdapter> Adapters = BuildAdapters();

    public static IReadOnlyCollection<string> KnownSources => Adapters.Keys.ToArray();

    public static bool IsKnownSource(string? source) =>
        TryGet(source, out _);

    public static bool TryGet(string? source, out ICodeIslandSourceAdapter adapter)
    {
        if (!string.IsNullOrWhiteSpace(source) &&
            Adapters.TryGetValue(source.Trim(), out adapter!))
        {
            return true;
        }

        adapter = UnknownAdapter;
        return false;
    }

    public static ICodeIslandSourceAdapter Get(string? source) =>
        TryGet(source, out var adapter) ? adapter : UnknownAdapter;

    private static Dictionary<string, ICodeIslandSourceAdapter> BuildAdapters()
    {
        return new Dictionary<string, ICodeIslandSourceAdapter>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new BuiltInSourceAdapter("claude", "Claude Code", "claude", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["codex"] = new BuiltInSourceAdapter("codex", "Codex", "codex", CodeIslandPermissionResponseStyle.Codex),
            ["gemini"] = new BuiltInSourceAdapter(
                "gemini",
                "Gemini CLI",
                "gemini",
                CodeIslandPermissionResponseStyle.ClaudeStyle,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BeforeTool"] = "PreToolUse",
                    ["AfterTool"] = "PostToolUse",
                    ["BeforeAgent"] = "SubagentStart",
                    ["AfterAgent"] = "SubagentStop"
                }),
            ["cursor"] = new BuiltInSourceAdapter(
                "cursor",
                "Cursor",
                "cursor",
                CodeIslandPermissionResponseStyle.ClaudeStyle,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["beforeSubmitPrompt"] = "UserPromptSubmit",
                    ["beforeShellExecution"] = "PreToolUse",
                    ["afterShellExecution"] = "PostToolUse",
                    ["beforeMcpToolExecution"] = "PreToolUse",
                    ["afterMcpToolExecution"] = "PostToolUse",
                    ["subagentStart"] = "SubagentStart",
                    ["subagentStop"] = "SubagentStop"
                }),
            ["cursor-cli"] = new BuiltInSourceAdapter("cursor-cli", "Cursor CLI", "cursor", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["trae"] = new BuiltInSourceAdapter("trae", "Trae", "trae", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["traecn"] = new BuiltInSourceAdapter("traecn", "Trae CN", "trae", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["traecli"] = new BuiltInSourceAdapter("traecli", "TraeCli", "traecli", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["copilot"] = new BuiltInSourceAdapter("copilot", "GitHub Copilot", "copilot", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["qoder"] = new BuiltInSourceAdapter("qoder", "Qoder", "qoder", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["qoder-cli"] = new BuiltInSourceAdapter("qoder-cli", "Qoder CLI", "qoder", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["droid"] = new BuiltInSourceAdapter("droid", "Factory", "factory", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["codebuddy"] = new BuiltInSourceAdapter("codebuddy", "CodeBuddy", "codebuddy", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["codybuddycn"] = new BuiltInSourceAdapter("codybuddycn", "CodyBuddy CN", "codebuddy", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["stepfun"] = new BuiltInSourceAdapter("stepfun", "StepFun", "stepfun", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["opencode"] = new BuiltInSourceAdapter("opencode", "OpenCode", "opencode", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["antigravity"] = new BuiltInSourceAdapter("antigravity", "AntiGravity", "antigravity", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["workbuddy"] = new BuiltInSourceAdapter("workbuddy", "WorkBuddy", "workbuddy", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["hermes"] = new BuiltInSourceAdapter("hermes", "Hermes", "hermes", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["qwen"] = new BuiltInSourceAdapter("qwen", "Qwen Code", "qwen", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["kimi"] = new BuiltInSourceAdapter("kimi", "Kimi Code", "kimi", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["pi"] = new BuiltInSourceAdapter("pi", "Pi", "pi", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["kiro"] = new BuiltInSourceAdapter("kiro", "Kiro", "kiro", CodeIslandPermissionResponseStyle.ClaudeStyle),
            ["cline"] = new BuiltInSourceAdapter(
                "cline",
                "Cline",
                "cline",
                CodeIslandPermissionResponseStyle.ClaudeStyle,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TaskStart"] = "SessionStart",
                    ["TaskResume"] = "UserPromptSubmit",
                    ["TaskComplete"] = "Stop"
                })
        };
    }

    private sealed class BuiltInSourceAdapter : ICodeIslandSourceAdapter
    {
        private readonly IReadOnlyDictionary<string, string> _eventAliases;

        public BuiltInSourceAdapter(
            string sourceKey,
            string displayName,
            string iconName,
            CodeIslandPermissionResponseStyle permissionResponseStyle,
            IReadOnlyDictionary<string, string>? eventAliases = null)
        {
            SourceKey = sourceKey;
            DisplayName = displayName;
            IconName = iconName;
            PermissionResponseStyle = permissionResponseStyle;
            _eventAliases = eventAliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string SourceKey { get; }

        public string DisplayName { get; }

        public string IconName { get; }

        public CodeIslandPermissionResponseStyle PermissionResponseStyle { get; }

        public bool TryNormalizeEventName(string rawEventName, out string normalizedEventName)
        {
            if (_eventAliases.TryGetValue(rawEventName.Trim(), out normalizedEventName!))
                return true;

            normalizedEventName = string.Empty;
            return false;
        }
    }
}
