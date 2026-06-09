using System.Text.Json;
using CodeIsland.Core.Services;

namespace CodeIsland.Core.Models;

/// <summary>
/// 单个 AI 工具会话的快照状态
/// </summary>
public class SessionSnapshot
{
    public string SessionId { get; set; } = "";
    public string Source { get; set; } = "";
    public string? ProjectName { get; set; }
    public string? WorkingDirectory { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Idle;
    public string? CurrentToolName { get; set; }
    public string? CurrentToolDescription { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public int Pid { get; set; }
    public DateTime? TrackedProcessStartedAtUtc { get; set; }
    public List<ToolHistoryEntry> ToolHistory { get; set; } = new();
    public List<ChatMessage> RecentMessages { get; set; } = new();
    public string? LastUserPrompt { get; set; }
    public string? LastAssistantMessage { get; set; }
    public string? CompletionText { get; set; }
    public bool Interrupted { get; set; }
    public string? TranscriptPath { get; set; }
    public long TranscriptPosition { get; set; }
    public string? TerminalApp { get; set; }
    public string? TerminalSessionId { get; set; }

    /// <summary>
    /// 克隆当前快照
    /// </summary>
    public SessionSnapshot Clone() => new()
    {
        SessionId = SessionId,
        Source = Source,
        ProjectName = ProjectName,
        WorkingDirectory = WorkingDirectory,
        Status = Status,
        CurrentToolName = CurrentToolName,
        CurrentToolDescription = CurrentToolDescription,
        CreatedAt = CreatedAt,
        LastUpdatedAt = DateTime.UtcNow,
        Pid = Pid,
        TrackedProcessStartedAtUtc = TrackedProcessStartedAtUtc,
        ToolHistory = new List<ToolHistoryEntry>(ToolHistory),
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

    /// <summary>
    /// 纯函数 reducer：根据事件计算新状态和副作用
    /// </summary>
    public static (SessionSnapshot NewState, SideEffect Effect) ReduceEvent(
        SessionSnapshot? current, HookEvent evt)
    {
        var state = ApplyEventMetadata(current ?? new SessionSnapshot
        {
            SessionId = evt.SessionId ?? Guid.NewGuid().ToString("N")[..8],
            Source = IsKnownSource(evt.Source) ? evt.Source! : "unknown",
            CreatedAt = DateTime.UtcNow
        }, evt);

        var normalizedEvent = EventNormalizer.NormalizeEventName(state.Source, evt.EventName);

        var result = normalizedEvent switch
        {
            "UserPromptSubmit" => HandleUserPromptSubmit(state, evt),

            "PreToolUse" when HookToolClassifier.ShouldBlockQuestionTool(evt, "PreToolUse") => (CloneWith(state, AgentStatus.WaitingQuestion,
                currentToolName: HookToolClassifier.GetToolName(evt) ?? evt.ToolName,
                currentToolDescription: FormatToolDescription(evt)),
                new SideEffect.ShowQuestionCard(state.SessionId, ExtractQuestionData(state.SessionId, evt))),

            "PreToolUse" when HasApprovalNeededSignal(evt) => (CloneWith(state, AgentStatus.WaitingApproval,
                currentToolName: HookToolClassifier.GetToolName(evt) ?? evt.ToolName,
                currentToolDescription: FormatToolDescription(evt)),
                new SideEffect.ShowApprovalCard(state.SessionId, BuildPermissionRequest(state, evt, "PreToolUse"))),

            "PreToolUse" => (CloneWith(state, AgentStatus.Running,
                currentToolName: HookToolClassifier.GetToolName(evt) ?? evt.ToolName,
                currentToolDescription: FormatToolDescription(evt)),
                new SideEffect.None()),

            "PostToolUse" => HandlePostToolUse(state, evt, success: true),

            "PostToolUseFailure" => HandlePostToolUse(state, evt, success: false),

            "PermissionDenied" => (ClearCurrentTool(CloneWith(state, AgentStatus.Processing)),
                new SideEffect.None()),

            "Stop" => HandleStop(state, evt),

            "SessionEnd" => (ClearCurrentTool(CloneWith(state, AgentStatus.Idle)),
                new SideEffect.None()),

            "SessionStart" => HandleSessionStart(state, evt),

            "SubagentStart" => (CloneWith(state, AgentStatus.Running,
                    currentToolName: "Agent",
                    currentToolDescription: GetStringField(evt.RawJson, "agent_type", "agentType", "agent")),
                new SideEffect.None()),

            "SubagentStop" => (ClearCurrentTool(CloneWith(state, AgentStatus.Processing)),
                new SideEffect.None()),

            "PreCompact" => (CloneWith(state, AgentStatus.Running,
                    currentToolName: "Compact",
                    currentToolDescription: "压缩上下文"),
                new SideEffect.None()),

            "PostCompact" => (ClearCurrentTool(CloneWith(state, AgentStatus.Processing)),
                new SideEffect.None()),

            "PermissionRequest" when HookToolClassifier.ShouldBlockQuestionTool(evt, "PermissionRequest") => (CloneWith(state, AgentStatus.WaitingQuestion),
                new SideEffect.ShowQuestionCard(state.SessionId, ExtractQuestionData(state.SessionId, evt))),

            "PermissionRequest" => (CloneWith(state, AgentStatus.WaitingApproval),
                new SideEffect.ShowApprovalCard(state.SessionId, BuildPermissionRequest(state, evt, "PermissionRequest"))),

            var eventName when IsQuestionEvent(eventName, evt) => (CloneWith(state, AgentStatus.WaitingQuestion),
                new SideEffect.ShowQuestionCard(state.SessionId,
                    ExtractQuestionData(state.SessionId, evt))),

            _ => (state.Clone(), new SideEffect.None())
        };

        return (ApplyEventMetadata(result.Item1, evt), result.Item2);
    }

    private static SessionSnapshot CloneWith(SessionSnapshot source,
        AgentStatus? status = null,
        string? currentToolName = null,
        string? currentToolDescription = null)
    {
        var clone = source.Clone();
        if (status.HasValue) clone.Status = status.Value;
        if (currentToolName != null) clone.CurrentToolName = currentToolName;
        if (currentToolDescription != null) clone.CurrentToolDescription = currentToolDescription;
        clone.LastUpdatedAt = DateTime.UtcNow;
        return clone;
    }

    private static SessionSnapshot ClearCurrentTool(SessionSnapshot state)
    {
        state.CurrentToolName = null;
        state.CurrentToolDescription = null;
        return state;
    }

    private static readonly string[] SystemPromptPlaceholders =
    {
        "<local-command-stdout>",
        "<local-command-stderr>",
        "<command-name>",
        "<command-message>",
        "<command-args>"
    };

    private static bool IsSystemPlaceholderPrompt(string prompt)
    {
        var trimmed = prompt.TrimStart();
        foreach (var marker in SystemPromptPlaceholders)
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static (SessionSnapshot, SideEffect) HandleUserPromptSubmit(SessionSnapshot state, HookEvent evt)
    {
        var clone = ClearCurrentTool(CloneWith(state, AgentStatus.Processing));
        var prompt = FirstStringFromEvent(evt, "prompt", "user_prompt", "userPrompt", "message", "input", "content", "text");

        if (string.IsNullOrWhiteSpace(prompt) || IsSystemPlaceholderPrompt(prompt))
            return (clone, new SideEffect.None());

        // 真用户输入：清空旧的完成态，标记新一轮开始
        clone.LastUserPrompt = prompt;
        clone.CompletionText = null;
        clone.LastAssistantMessage = null;
        AddRecentMessage(clone, new ChatMessage { IsUser = true, Text = prompt });

        return (clone, new SideEffect.None());
    }

    private static (SessionSnapshot, SideEffect) HandlePostToolUse(SessionSnapshot state, HookEvent evt, bool success)
    {
        var clone = state.Clone();
        var toolName = evt.ToolName ?? state.CurrentToolName;
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            clone.ToolHistory.Add(new ToolHistoryEntry
            {
                ToolName = toolName,
                Timestamp = DateTime.UtcNow,
                Description = FormatToolDescription(evt) ?? state.CurrentToolDescription,
                Success = success
            });
        }
        clone.Status = AgentStatus.Processing;
        clone.CurrentToolName = null;
        clone.CurrentToolDescription = null;
        clone.LastUpdatedAt = DateTime.UtcNow;
        return (clone, new SideEffect.None());
    }

    private static (SessionSnapshot, SideEffect) HandleStop(SessionSnapshot state, HookEvent evt)
    {
        var clone = ClearCurrentTool(CloneWith(state, AgentStatus.Idle));
        var stopReason = GetStringField(evt.RawJson, "stop_reason", "stopReason", "reason");
        clone.Interrupted = stopReason is not null &&
            (stopReason.Equals("user", StringComparison.OrdinalIgnoreCase) ||
             stopReason.Equals("interrupted", StringComparison.OrdinalIgnoreCase));

        var assistantMessage = FirstStringFromEvent(evt,
            "last_assistant_message", "lastAssistantMessage", "text", "message", "summary");
        if (!string.IsNullOrWhiteSpace(assistantMessage))
        {
            clone.LastAssistantMessage = assistantMessage;
            clone.CompletionText = assistantMessage;
            AddRecentMessage(clone, new ChatMessage { IsUser = false, Text = assistantMessage });
        }
        else
        {
            clone.CompletionText = clone.LastAssistantMessage;
        }

        return string.IsNullOrWhiteSpace(clone.CompletionText)
            ? (clone, new SideEffect.None())
            : (clone, new SideEffect.PlaySound("complete"));
    }

    private static (SessionSnapshot, SideEffect) HandleSessionStart(SessionSnapshot state, HookEvent evt)
    {
        var clone = ApplyEventMetadata(state.Clone(), evt);
        clone.Status = AgentStatus.Idle;
        clone.LastUpdatedAt = DateTime.UtcNow;
        clone.TerminalApp = evt.RawJson.TryGetProperty("_term_app", out var term) ? term.GetString() : clone.TerminalApp;
        clone.TerminalSessionId = evt.RawJson.TryGetProperty("_iterm_session", out var s) ? s.GetString() :
                                   evt.RawJson.TryGetProperty("WT_SESSION", out var wt) ? wt.GetString() :
                                   evt.RawJson.TryGetProperty("_wt_session", out var injectedWt) ? injectedWt.GetString() : clone.TerminalSessionId;
        return (clone, new SideEffect.PlaySound("start"));
    }

    private static SessionSnapshot ApplyEventMetadata(SessionSnapshot state, HookEvent evt)
    {
        var clone = state.Clone();

        if (IsKnownSource(evt.Source))
            clone.Source = evt.Source!;

        if (evt.TrackedPid is { } trackedPid)
        {
            var previousPid = clone.Pid;
            clone.Pid = trackedPid;
            if (evt.TrackedProcessStartedAtUtc is { } startedAtUtc)
                clone.TrackedProcessStartedAtUtc = startedAtUtc;
            else if (previousPid != trackedPid)
                clone.TrackedProcessStartedAtUtc = null;
        }
        else if (evt.ParentPid is { } parentPid)
        {
            clone.Pid = parentPid;
            clone.TrackedProcessStartedAtUtc = null;
        }

        var transcriptPath = TranscriptPathResolver.ExtractTranscriptPath(evt.RawJson) ??
                             TranscriptPathResolver.ExtractTranscriptPath(evt.ToolInput);
        if (!string.IsNullOrWhiteSpace(transcriptPath))
        {
            clone.TranscriptPath = transcriptPath;
        }
        else if (string.IsNullOrWhiteSpace(clone.TranscriptPath) &&
                 IsCodexSource(clone.Source) &&
                 !string.IsNullOrWhiteSpace(clone.SessionId))
        {
            var codexTranscriptPath = TranscriptPathResolver.TryResolveCodexTranscriptPath(clone.SessionId);
            if (!string.IsNullOrWhiteSpace(codexTranscriptPath))
                clone.TranscriptPath = codexTranscriptPath;
        }

        var workingDirectory = TranscriptPathResolver.ExtractWorkingDirectory(evt.RawJson) ??
                               TranscriptPathResolver.ExtractWorkingDirectory(evt.ToolInput);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            clone.WorkingDirectory = workingDirectory;
            clone.ProjectName = TranscriptPathResolver.ExtractProjectName(workingDirectory) ?? clone.ProjectName;
        }

        var explicitProjectName = GetStringField(evt.RawJson, "project_name", "projectName", "project", "workspace_name", "workspaceName");
        if (!string.IsNullOrWhiteSpace(explicitProjectName))
            clone.ProjectName = explicitProjectName;

        return clone;
    }

    private static bool IsKnownSource(string? source) =>
        !string.IsNullOrWhiteSpace(source) &&
        !source.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
        SupportedSource.IsValid(source);

    private static bool IsCodexSource(string? source) =>
        source?.Equals("codex", StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetStringField(JsonElement json, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (json.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }

        return null;
    }

    private static string? FirstStringFromEvent(HookEvent evt, params string[] keys) =>
        FirstStringFromElement(evt.RawJson, keys) ?? FirstStringFromElement(evt.ToolInput, keys);

    private static string? FirstStringFromElement(JsonElement? element, params string[] keys)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }

        foreach (var nestKey in new[] { "message", "payload", "data", "input", "params", "tool_input" })
        {
            if (obj.TryGetProperty(nestKey, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                var nestedValue = FirstStringFromElement(nested, keys);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                    return nestedValue;
            }
        }

        return null;
    }

    public static void AddRecentMessage(SessionSnapshot snapshot, ChatMessage message, int maxMessages = 6)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
            return;

        if (message.IsUser && IsSystemPlaceholderPrompt(message.Text))
            return;

        if (snapshot.RecentMessages.LastOrDefault()?.IsUser == message.IsUser &&
            snapshot.RecentMessages.LastOrDefault()?.Text == message.Text)
            return;

        snapshot.RecentMessages.Add(message);
        while (snapshot.RecentMessages.Count > maxMessages)
            snapshot.RecentMessages.RemoveAt(0);

        if (message.IsUser)
            snapshot.LastUserPrompt = message.Text;
        else
            snapshot.LastAssistantMessage = message.Text;
    }

    private static string? FormatToolDescription(HookEvent evt)
    {
        if (evt.ToolInput == null) return null;
        var input = evt.ToolInput.Value;
        return evt.ToolName switch
        {
            "Bash" => input.TryGetProperty("command", out var cmd) ? cmd.GetString() : null,
            "Read" => input.TryGetProperty("file_path", out var fp) ? fp.GetString() : null,
            "Edit" => input.TryGetProperty("file_path", out var fp) ? fp.GetString() : null,
            "Write" => input.TryGetProperty("file_path", out var fp) ? fp.GetString() : null,
            "Grep" => input.TryGetProperty("pattern", out var p) ? p.GetString() : null,
            "Glob" => input.TryGetProperty("pattern", out var p) ? p.GetString() : null,
            _ => null
        };
    }

    private static PermissionRequest BuildPermissionRequest(SessionSnapshot state, HookEvent evt, string hookEventName) => new()
    {
        SessionId = state.SessionId,
        ToolName = HookToolClassifier.GetToolName(evt) ?? state.CurrentToolName ?? "",
        ToolUseId = evt.ToolUseId,
        ToolInput = ExtractToolInputDictionary(evt),
        Description = FormatToolDescription(evt) ?? state.CurrentToolDescription,
        HookEventName = hookEventName
    };

    private static bool IsQuestionEvent(string normalizedEvent, HookEvent evt)
    {
        return (normalizedEvent == "Notification" ||
                normalizedEvent.Contains("Question", StringComparison.OrdinalIgnoreCase)) &&
               HasQuestion(evt);
    }

    private static bool HasQuestion(HookEvent evt)
    {
        return ContainsQuestion(evt.ToolInput) || ContainsQuestion(evt.RawJson);
    }

    private static bool HasApprovalNeededSignal(HookEvent evt)
    {
        return ContainsApprovalNeededSignal(evt.ToolInput) || ContainsApprovalNeededSignal(evt.RawJson);
    }

    private static bool ContainsApprovalNeededSignal(System.Text.Json.JsonElement? element)
    {
        if (element is not { ValueKind: System.Text.Json.JsonValueKind.Object } obj)
            return false;

        foreach (var prop in obj.EnumerateObject())
        {
            if (IsApprovalSignalName(prop.Name) && IsTruthyApprovalSignal(prop.Value))
                return true;

            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object && ContainsApprovalNeededSignal(prop.Value))
                return true;
        }

        return false;
    }

    private static bool IsApprovalSignalName(string name) =>
        name.Equals("permission_request", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("permissionRequest", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("requires_approval", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("requiresApproval", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("approval_required", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("approvalRequired", StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthyApprovalSignal(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.String => value.GetString() is { } text &&
                                                  !text.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                                                  !text.Equals("0", StringComparison.OrdinalIgnoreCase),
        System.Text.Json.JsonValueKind.Object => true,
        _ => false
    };

    private static bool ContainsQuestion(System.Text.Json.JsonElement? element)
    {
        if (element is not { ValueKind: System.Text.Json.JsonValueKind.Object } obj)
            return false;

        return obj.TryGetProperty("question", out _) || obj.TryGetProperty("questions", out _);
    }

    private static QuestionData ExtractQuestionData(string sessionId, HookEvent evt)
    {
        var input = GetQuestionPayload(evt);
        if (input == null)
            return new QuestionData { SessionId = sessionId };

        var questionItems = ExtractQuestionItems(input.Value);
        var firstItem = questionItems?.FirstOrDefault();
        var question = input.Value.TryGetProperty("question", out var q)
            ? q.GetString() ?? ""
            : ExtractQuestionTextFromQuestions(input.Value);
        var options = ExtractQuestionOptions(input.Value) ?? firstItem?.Options;
        var header = GetStringField(input.Value, "header", "title") ?? firstItem?.Header;
        var multiSelect = GetBooleanField(input.Value, "multiSelect", "multi_select", "multiple") ?? firstItem?.MultiSelect ?? false;

        return new QuestionData
        {
            SessionId = sessionId,
            Id = GetStringField(input.Value, "id"),
            Question = string.IsNullOrWhiteSpace(question) ? firstItem?.Question ?? "" : question,
            Header = header,
            Options = options,
            MultiSelect = multiSelect,
            IsMultiQuestion = questionItems is { Count: > 1 },
            Questions = questionItems,
            HookEventName = EventNormalizer.NormalizeEventName(evt.Source ?? "unknown", evt.EventName),
            IsAskUserQuestion = HookToolClassifier.IsAskUserQuestion(evt),
            IsCodexRequestUserInput = HookToolClassifier.IsCodexRequestUserInput(evt),
            OriginalInput = input.Value.Clone()
        };
    }

    private static System.Text.Json.JsonElement? GetQuestionPayload(HookEvent evt)
    {
        if (ContainsQuestion(evt.ToolInput))
            return evt.ToolInput;
        if (ContainsQuestion(evt.RawJson))
            return evt.RawJson;
        return null;
    }

    private static string ExtractQuestionTextFromQuestions(System.Text.Json.JsonElement input)
    {
        if (!input.TryGetProperty("questions", out var questions))
            return "";

        if (questions.ValueKind == System.Text.Json.JsonValueKind.String)
            return questions.GetString() ?? "";

        if (questions.ValueKind != System.Text.Json.JsonValueKind.Array)
            return "";

        var texts = new List<string>();
        foreach (var question in questions.EnumerateArray())
        {
            if (question.ValueKind == System.Text.Json.JsonValueKind.String)
                texts.Add(question.GetString() ?? "");
            else if (question.ValueKind == System.Text.Json.JsonValueKind.Object &&
                     question.TryGetProperty("question", out var nestedQuestion))
                texts.Add(nestedQuestion.GetString() ?? "");
        }

        return string.Join(Environment.NewLine, texts.Where(static text => !string.IsNullOrWhiteSpace(text)));
    }

    private static List<QuestionItem>? ExtractQuestionItems(System.Text.Json.JsonElement input)
    {
        if (!input.TryGetProperty("questions", out var questions) || questions.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;

        var items = new List<QuestionItem>();
        foreach (var question in questions.EnumerateArray())
        {
            if (question.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var text = question.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                    items.Add(new QuestionItem { Question = text, AllowFreeText = true });
                continue;
            }

            if (question.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            var textValue = GetStringField(question, "question", "text", "prompt") ?? "";
            if (string.IsNullOrWhiteSpace(textValue))
                continue;

            items.Add(new QuestionItem
            {
                Id = GetStringField(question, "id"),
                Question = textValue,
                Header = GetStringField(question, "header", "title"),
                Options = ExtractQuestionOptions(question),
                MultiSelect = GetBooleanField(question, "multiSelect", "multi_select", "multiple") ?? false,
                AllowFreeText = true
            });
        }

        return items.Count > 0 ? items : null;
    }

    private static bool? GetBooleanField(System.Text.Json.JsonElement json, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!json.TryGetProperty(key, out var prop))
                continue;

            if (prop.ValueKind == System.Text.Json.JsonValueKind.True)
                return true;
            if (prop.ValueKind == System.Text.Json.JsonValueKind.False)
                return false;
            if (prop.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(prop.GetString(), out var value))
                return value;
        }

        return null;
    }

    private static List<QuestionOption>? ExtractQuestionOptions(System.Text.Json.JsonElement input)
    {
        if (!input.TryGetProperty("options", out var optionsElement) ||
            optionsElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;

        var options = new List<QuestionOption>();
        foreach (var option in optionsElement.EnumerateArray())
        {
            if (option.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var label = option.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(label))
                    options.Add(new QuestionOption { Label = label, Value = label });
            }
            else if (option.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var label = option.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? "" :
                            option.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" :
                            option.TryGetProperty("value", out var valueProp) ? valueProp.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(label))
                    continue;

                options.Add(new QuestionOption
                {
                    Label = label,
                    Description = option.TryGetProperty("description", out var descProp) ? descProp.GetString() : null,
                    Value = option.TryGetProperty("value", out var valProp) ? valProp.GetString() : label
                });
            }
        }

        return options.Count > 0 ? options : null;
    }

    private static Dictionary<string, object?>? ExtractToolInputDictionary(HookEvent evt)
    {
        if (evt.ToolInput?.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        var result = new Dictionary<string, object?>();
        foreach (var prop in evt.ToolInput.Value.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                System.Text.Json.JsonValueKind.Number when prop.Value.TryGetInt64(out var longValue) => longValue,
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                _ => prop.Value.Clone()
            };
        }
        return result;
    }
}

/// <summary>
/// 工具调用历史条目
/// </summary>
public class ToolHistoryEntry
{
    public string ToolName { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string? Description { get; init; }
    public bool Success { get; init; } = true;
}

/// <summary>
/// 副作用类型
/// </summary>
public abstract record SideEffect
{
    public record None : SideEffect;
    public record PlaySound(string SoundName) : SideEffect;
    public record ShowApprovalCard(string SessionId, PermissionRequest Request) : SideEffect;
    public record ShowQuestionCard(string SessionId, QuestionData Question) : SideEffect;
    public record JumpToTerminal(string SessionId) : SideEffect;
    public record SendResponse(string ResponseJson) : SideEffect;
}
