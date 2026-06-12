using CodeIsland.Contracts;
using CodeIsland.Core.Models;
using CodeIsland.WpfApp.ViewModels;
using CodeIsland.Hub;

namespace CodeIsland.WpfApp.Services;

public sealed class WpfHubStateAdapter : ICodeIslandHubState
{
    private readonly WpfAppState _state;

    public WpfHubStateAdapter(WpfAppState state)
    {
        _state = state;
    }

    public IReadOnlyList<SessionDto> GetSessions() =>
        _state.GetApiSessionSnapshots().Select(MapSession).ToList();

    public SessionDto? GetSession(string sessionId) =>
        _state.GetApiSessionSnapshot(sessionId) is { } session ? MapSession(session) : null;

    public IReadOnlyList<ChatMessageDto> GetSessionMessages(string sessionId) =>
        _state.GetApiSessionSnapshot(sessionId)?.RecentMessages.Select(MapMessage).ToList() ?? [];

    public IReadOnlyList<PendingActionDto> GetPendingActions() =>
        _state.GetApiPendingActions().Select(MapPendingAction).ToList();

    public PendingActionDto? GetPendingAction(string actionId) =>
        _state.GetApiPendingAction(actionId) is { } pending ? MapPendingAction(pending) : null;

    public bool DismissSession(string sessionId) =>
        _state.DismissSessionFromApi(sessionId);

    public bool ActivateTerminal(string sessionId) =>
        _state.ActivateTerminalFromApi(sessionId);

    public bool AllowPermission(string actionId, bool always) =>
        _state.TryAllowPermissionAction(actionId, always);

    public bool DenyPermission(string actionId, string reason) =>
        _state.TryDenyPermissionAction(actionId, reason);

    public bool AnswerQuestion(string actionId, QuestionAnswerRequest request) =>
        _state.TryAnswerQuestionAction(actionId, request.Answers, request.Answer);

    public bool DismissQuestion(string actionId, string reason) =>
        _state.TryDismissQuestionAction(actionId, reason);

    private static SessionDto MapSession(SessionSnapshot session) => new(
        session.SessionId,
        session.Source,
        SupportedSource.GetDisplayName(session.Source),
        session.ProjectName,
        session.WorkingDirectory,
        session.Status.ToString(),
        session.CurrentToolName,
        session.CurrentToolDescription,
        AsUtc(session.CreatedAt),
        AsUtc(session.LastUpdatedAt),
        session.Pid == 0 ? null : session.Pid,
        session.TrackedProcessStartedAtUtc is { } startedAt ? AsUtc(startedAt) : null,
        session.LastUserPrompt,
        session.LastAssistantMessage,
        session.CompletionText,
        session.TranscriptPath,
        session.TranscriptPosition,
        session.RecentMessages.Select(MapMessage).ToList(),
        session.ToolHistory.Select(MapToolHistory).ToList());

    private static PendingActionDto MapPendingAction(WpfPendingActionState pending) => new(
        pending.ActionId,
        pending.Kind,
        pending.SessionId,
        pending.Source,
        SupportedSource.GetDisplayName(pending.Source),
        pending.ProjectName,
        pending.WorkingDirectory,
        AsUtc(pending.CreatedAt),
        pending.Permission is { } permission ? MapPermission(permission) : null,
        pending.Question is { } question ? MapQuestion(question, pending.CurrentQuestionIndex, pending.CurrentAnswerKey) : null);

    private static PermissionRequestDto MapPermission(PermissionRequest request) => new(
        request.SessionId,
        request.ToolName,
        request.ToolUseId,
        request.Description,
        request.HookEventName);

    private static QuestionDto MapQuestion(QuestionData question, int currentQuestionIndex, string? currentAnswerKey) => new(
        question.SessionId,
        question.Id,
        question.Question,
        question.Header,
        (question.Options ?? []).Select(MapQuestionOption).ToList(),
        question.MultiSelect,
        question.IsMultiQuestion,
        (question.Questions ?? []).Select(MapQuestionItem).ToList(),
        question.HookEventName,
        question.IsAskUserQuestion,
        question.IsCodexRequestUserInput,
        currentQuestionIndex,
        currentAnswerKey ?? question.Id ?? question.Question);

    private static QuestionItemDto MapQuestionItem(QuestionItem item) => new(
        item.Id,
        item.Question,
        item.Header,
        (item.Options ?? []).Select(MapQuestionOption).ToList(),
        item.MultiSelect,
        item.AllowFreeText);

    private static QuestionOptionDto MapQuestionOption(QuestionOption option) => new(
        option.Label,
        option.Description,
        option.Value);

    private static ChatMessageDto MapMessage(ChatMessage message) => new(
        message.IsUser,
        message.Text,
        AsUtc(message.Timestamp));

    private static ToolHistoryEntryDto MapToolHistory(ToolHistoryEntry entry) => new(
        entry.ToolName,
        AsUtc(entry.Timestamp),
        entry.Description,
        entry.Success);

    private static DateTimeOffset AsUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc);
    }
}
