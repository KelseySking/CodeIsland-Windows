namespace CodeIsland.WpfApp.Models;

public enum AgentStatus
{
    Idle,
    Processing,
    Running,
    WaitingQuestion,
    WaitingApproval,
    Completed,
    Error
}

public sealed class SessionSnapshot
{
    public string SessionId { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceDisplayName { get; set; } = "";
    public string? ProjectName { get; set; }
    public string? WorkingDirectory { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Idle;
    public string? CurrentToolName { get; set; }
    public string? CurrentToolDescription { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public int Pid { get; set; }
    public DateTime? TrackedProcessStartedAtUtc { get; set; }
    public List<ToolHistoryEntry> ToolHistory { get; set; } = [];
    public List<ChatMessage> RecentMessages { get; set; } = [];
    public string? LastUserPrompt { get; set; }
    public string? LastAssistantMessage { get; set; }
    public string? CompletionText { get; set; }
    public bool Interrupted { get; set; }
    public string? TranscriptPath { get; set; }
    public long TranscriptPosition { get; set; }
    public string? TerminalApp { get; set; }
    public string? TerminalSessionId { get; set; }

    public SessionSnapshot Clone() => new()
    {
        SessionId = SessionId,
        Source = Source,
        SourceDisplayName = SourceDisplayName,
        ProjectName = ProjectName,
        WorkingDirectory = WorkingDirectory,
        Status = Status,
        CurrentToolName = CurrentToolName,
        CurrentToolDescription = CurrentToolDescription,
        CreatedAt = CreatedAt,
        LastUpdatedAt = LastUpdatedAt,
        Pid = Pid,
        TrackedProcessStartedAtUtc = TrackedProcessStartedAtUtc,
        ToolHistory = ToolHistory.Select(static entry => new ToolHistoryEntry
        {
            ToolName = entry.ToolName,
            Timestamp = entry.Timestamp,
            Description = entry.Description,
            Success = entry.Success
        }).ToList(),
        RecentMessages = RecentMessages.Select(static message => new ChatMessage
        {
            IsUser = message.IsUser,
            Text = message.Text,
            Timestamp = message.Timestamp
        }).ToList(),
        LastUserPrompt = LastUserPrompt,
        LastAssistantMessage = LastAssistantMessage,
        CompletionText = CompletionText,
        Interrupted = Interrupted,
        TranscriptPath = TranscriptPath,
        TranscriptPosition = TranscriptPosition,
        TerminalApp = TerminalApp,
        TerminalSessionId = TerminalSessionId
    };

    public static void AddRecentMessage(SessionSnapshot snapshot, ChatMessage message, int maxMessages = 6)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
            return;

        if (snapshot.RecentMessages.LastOrDefault()?.IsUser == message.IsUser &&
            snapshot.RecentMessages.LastOrDefault()?.Text == message.Text)
        {
            return;
        }

        snapshot.RecentMessages.Add(message);
        while (snapshot.RecentMessages.Count > maxMessages)
            snapshot.RecentMessages.RemoveAt(0);

        if (message.IsUser)
            snapshot.LastUserPrompt = message.Text;
        else
            snapshot.LastAssistantMessage = message.Text;
    }
}

public sealed class ToolHistoryEntry
{
    public string ToolName { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string? Description { get; init; }
    public bool Success { get; init; } = true;
}

public sealed class ChatMessage
{
    public bool IsUser { get; init; }
    public string Text { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class PermissionRequest
{
    public string SessionId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string? ToolUseId { get; init; }
    public Dictionary<string, object?>? ToolInput { get; init; }
    public string? Description { get; init; }
    public string HookEventName { get; init; } = "PermissionRequest";
    public bool IsSafeInternalTool => ToolName is "Read" or "Grep" or "Glob" or "LS";
}

public sealed class QuestionData
{
    public string SessionId { get; init; } = "";
    public string? Id { get; init; }
    public string Question { get; init; } = "";
    public string? Header { get; init; }
    public List<QuestionOption>? Options { get; init; }
    public bool MultiSelect { get; init; }
    public bool IsMultiQuestion { get; init; }
    public List<QuestionItem>? Questions { get; init; }
    public string HookEventName { get; init; } = "";
    public bool IsAskUserQuestion { get; init; }
    public bool IsCodexRequestUserInput { get; init; }
}

public sealed class QuestionOption
{
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public string? Value { get; init; }
}

public sealed class QuestionItem
{
    public string? Id { get; init; }
    public string Question { get; init; } = "";
    public string? Header { get; init; }
    public List<QuestionOption>? Options { get; init; }
    public bool MultiSelect { get; init; }
    public bool AllowFreeText { get; init; }
}

public sealed record WpfPendingActionSnapshot(
    string ActionId,
    string Kind,
    DateTime CreatedAt,
    string SessionId,
    string Source,
    string? ProjectName,
    string? WorkingDirectory,
    PermissionRequest? Permission,
    QuestionData? Question,
    int CurrentQuestionIndex = 0,
    string? CurrentAnswerKey = null);

public sealed class WpfRuntimeStateChangedEventArgs : EventArgs
{
    public WpfRuntimeStateChangedEventArgs(
        IReadOnlyList<SessionSnapshot> sessions,
        IReadOnlyList<WpfPendingActionSnapshot> pendingActions,
        string? affectedSessionId,
        string? affectedActionId,
        string? normalizedEventName,
        SideEffect effect,
        string? realtimeEventType)
    {
        Sessions = sessions;
        PendingActions = pendingActions;
        AffectedSessionId = affectedSessionId;
        AffectedActionId = affectedActionId;
        NormalizedEventName = normalizedEventName;
        Effect = effect;
        RealtimeEventType = realtimeEventType;
    }

    public IReadOnlyList<SessionSnapshot> Sessions { get; }
    public IReadOnlyList<WpfPendingActionSnapshot> PendingActions { get; }
    public string? AffectedSessionId { get; }
    public string? AffectedActionId { get; }
    public string? NormalizedEventName { get; }
    public SideEffect Effect { get; }
    public string? RealtimeEventType { get; }
}

public abstract record SideEffect
{
    public record None : SideEffect;
    public record PlaySound(string SoundName) : SideEffect;
    public record ShowApprovalCard(string SessionId, PermissionRequest Request) : SideEffect;
    public record ShowQuestionCard(string SessionId, QuestionData Question) : SideEffect;
}

public static class WpfSourceDisplay
{
    private static readonly IReadOnlyDictionary<string, string> CliIconNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["antigravity"] = "antigravity",
        ["claude"] = "claude",
        ["claudecode"] = "claude",
        ["claudecli"] = "claude",
        ["cline"] = "cline",
        ["codebuddy"] = "codebuddy",
        ["codex"] = "codex",
        ["codexcli"] = "codex",
        ["openaicodex"] = "codex",
        ["copilot"] = "copilot",
        ["githubcopilot"] = "copilot",
        ["cursor"] = "cursor",
        ["cursorcli"] = "cursor",
        ["factory"] = "factory",
        ["factoryai"] = "factory",
        ["gemini"] = "gemini",
        ["geminicli"] = "gemini",
        ["googlegemini"] = "gemini",
        ["hermes"] = "hermes",
        ["kimi"] = "kimi",
        ["opencode"] = "opencode",
        ["pi"] = "pi",
        ["qoder"] = "qoder",
        ["qwen"] = "qwen",
        ["qwencode"] = "qwen",
        ["stepfun"] = "stepfun",
        ["stepfunai"] = "stepfun",
        ["trae"] = "trae",
        ["traeai"] = "trae",
        ["workbuddy"] = "workbuddy"
    };

    public static string GetDisplayName(string? source, string? providedDisplayName = null)
    {
        if (!string.IsNullOrWhiteSpace(providedDisplayName))
            return providedDisplayName;

        return source?.ToLowerInvariant() switch
        {
            "claude" => "Claude Code",
            "codex" => "Codex",
            "codeisland" => "CodeIsland",
            "unknown" or null or "" => "未知工具",
            _ => source
        };
    }

    public static string GetIconName(string? source)
    {
        var iconName = GetCliIconName(source);
        if (!string.IsNullOrWhiteSpace(iconName))
            return iconName;

        return string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    }

    public static string? GetCliIconName(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var key = NormalizeIconKey(candidate);
            if (key.Length > 0 && CliIconNames.TryGetValue(key, out var iconName))
                return iconName;
        }

        return null;
    }

    public static string? GetCliIconUri(params string?[] candidates)
    {
        var iconName = GetCliIconName(candidates);
        return string.IsNullOrWhiteSpace(iconName)
            ? null
            : $"pack://application:,,,/Assets/cli-icons/{Uri.EscapeDataString(iconName)}.png";
    }

    private static string NormalizeIconKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Trim();
        var lastSeparator = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\'));
        if (lastSeparator >= 0 && lastSeparator < normalized.Length - 1)
            normalized = normalized[(lastSeparator + 1)..];
        if (normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        return new string(normalized.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
