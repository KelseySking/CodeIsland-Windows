using CodeIsland.Core.Models;
using CodeIsland.Core.Sources;

namespace CodeIsland.Core.Services;

public static class HookResponseBuilder
{
    public static string BuildPermissionAllowResponse(HookEvent evt, PermissionRequest? request = null, bool always = false) =>
        CodeIslandSourceAdapterRegistry.Get(evt.Source).PermissionResponseStyle == CodeIslandPermissionResponseStyle.Codex
            ? CodexHookResponseBuilder.BuildPermissionAllowResponse(evt, request, always)
            : ClaudeStyleHookResponseBuilder.BuildPermissionAllowResponse(evt, always);

    public static string BuildPermissionDenyResponse(HookEvent evt, string reason) =>
        CodeIslandSourceAdapterRegistry.Get(evt.Source).PermissionResponseStyle == CodeIslandPermissionResponseStyle.Codex
            ? CodexHookResponseBuilder.BuildPermissionDenyResponse(evt, reason)
            : ClaudeStyleHookResponseBuilder.BuildPermissionDenyResponse(evt, reason);

    public static string BuildQuestionAnswerResponse(
        HookEvent evt,
        QuestionData question,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        if (question.IsCodexRequestUserInput || HookToolClassifier.IsCodexRequestUserInput(evt))
            return CodexHookResponseBuilder.BuildRequestUserInputAnswerResponse(evt, answers);

        if (question.IsAskUserQuestion || HookToolClassifier.IsAskUserQuestion(evt))
            return ClaudeStyleHookResponseBuilder.BuildAskUserQuestionAnswerResponse(evt, question, answers);

        return LegacyQuestionResponseBuilder.BuildQuestionAnswerResponse(question, answers);
    }

    public static string BuildQuestionDismissResponse(HookEvent evt, string reason) =>
        HookToolClassifier.IsCodexRequestUserInput(evt)
            ? CodexHookResponseBuilder.BuildRequestUserInputDismissResponse(evt, reason)
            : LegacyQuestionResponseBuilder.BuildQuestionDismissResponse(reason);
}
