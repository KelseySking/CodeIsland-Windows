namespace CodeIsland.Contracts;

public sealed record ApiHealthDto(string Status, DateTimeOffset StartedAtUtc);

public sealed record ApiVersionDto(string Product, string Version);

public sealed record ApiCapabilitiesDto(
    bool HookInjection,
    bool Approval,
    bool Question,
    bool Transcript,
    bool Realtime,
    IReadOnlyList<string> RealtimeProtocols,
    string SecurityMode);

public sealed record ApiErrorDto(string Code, string Message);

public sealed record RuntimeAssetsDto(
    string RuntimeDirectory,
    string HookScriptPath,
    string BridgeExePath,
    bool Installed);

public sealed record SourceCapabilitiesDto(
    bool HookInstall,
    bool Approval,
    bool Question,
    bool Transcript,
    bool AlwaysAllow);

public sealed record SourceDto(
    string Id,
    string DisplayName,
    string IconName,
    bool Installed,
    SourceCapabilitiesDto Capabilities);

public sealed record SourceStatusDto(
    string Source,
    bool Supported,
    bool Installed,
    string DisplayName);

public sealed record SourceOperationResultDto(
    string Source,
    bool Success,
    bool Installed,
    string Message);

public sealed record ChatMessageDto(
    bool IsUser,
    string Text,
    DateTimeOffset TimestampUtc);

public sealed record ToolHistoryEntryDto(
    string ToolName,
    DateTimeOffset TimestampUtc,
    string? Description,
    bool Success);

public sealed record SessionDto(
    string SessionId,
    string Source,
    string SourceDisplayName,
    string? ProjectName,
    string? WorkingDirectory,
    string Status,
    string? CurrentToolName,
    string? CurrentToolDescription,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUpdatedAtUtc,
    int? TrackedPid,
    DateTimeOffset? TrackedProcessStartedAtUtc,
    string? LastUserPrompt,
    string? LastAssistantMessage,
    string? CompletionText,
    string? TranscriptPath,
    long TranscriptPosition,
    string? TerminalApp,
    string? TerminalSessionId,
    IReadOnlyList<ChatMessageDto> RecentMessages,
    IReadOnlyList<ToolHistoryEntryDto> ToolHistory);

public sealed record PermissionRequestDto(
    string SessionId,
    string ToolName,
    string? ToolUseId,
    IReadOnlyDictionary<string, object?>? ToolInput,
    string? Description,
    string HookEventName);

public sealed record QuestionOptionDto(
    string Label,
    string? Description,
    string? Value);

public sealed record QuestionItemDto(
    string? Id,
    string Question,
    string? Header,
    IReadOnlyList<QuestionOptionDto> Options,
    bool MultiSelect,
    bool AllowFreeText);

public sealed record QuestionDto(
    string SessionId,
    string? Id,
    string Question,
    string? Header,
    IReadOnlyList<QuestionOptionDto> Options,
    bool MultiSelect,
    bool IsMultiQuestion,
    IReadOnlyList<QuestionItemDto> Questions,
    string HookEventName,
    bool IsAskUserQuestion,
    bool IsCodexRequestUserInput,
    int CurrentQuestionIndex,
    string CurrentAnswerKey);

public sealed record PendingActionDto(
    string ActionId,
    string Kind,
    string SessionId,
    string Source,
    string SourceDisplayName,
    string? ProjectName,
    string? WorkingDirectory,
    DateTimeOffset CreatedAtUtc,
    PermissionRequestDto? Permission,
    QuestionDto? Question);

public sealed record PermissionDecisionRequest(bool Always = false, string? Reason = null);

public sealed record QuestionAnswerRequest(
    string? Answer = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers = null);

public sealed record QuestionCurrentAnswerRequest(
    IReadOnlyList<string> Answers);

public sealed record QuestionCurrentAnswerResultDto(
    bool Success,
    bool Resolved);

public sealed record HubEventDto(
    string Type,
    DateTimeOffset TimestampUtc,
    object? Data);
