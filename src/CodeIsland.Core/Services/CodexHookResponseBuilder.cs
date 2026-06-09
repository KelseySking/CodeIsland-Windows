using System.Text.Json.Nodes;
using CodeIsland.Core.Models;

namespace CodeIsland.Core.Services;

internal static class CodexHookResponseBuilder
{
    public static string BuildPermissionAllowResponse(HookEvent evt, PermissionRequest? request, bool always)
    {
        if (always && request != null)
            CodexPermissionRules.TryAppendAllowRule(request);

        return BuildApprovalDecisionResponse(evt, "allow", reason: null, always);
    }

    public static string BuildPermissionDenyResponse(HookEvent evt, string reason) =>
        BuildApprovalDecisionResponse(evt, "deny", reason, always: false);

    public static string BuildRequestUserInputAnswerResponse(
        HookEvent evt,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        if (GetHookEventName(evt) == "PreToolUse")
            return BuildPreToolUseDenyResponse(BuildRequestUserInputAnswerReason(answers));

        return BuildRequestUserInputDecisionResponse(evt, "allow", reason: null);
    }

    public static string BuildRequestUserInputDismissResponse(HookEvent evt, string reason) =>
        GetHookEventName(evt) == "PreToolUse"
            ? BuildPreToolUseDenyResponse(BuildRequestUserInputDismissReason(reason))
            : BuildRequestUserInputDecisionResponse(evt, "deny", reason);

    private static string BuildRequestUserInputAnswerReason(IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var response = new JsonObject
        {
            ["answers"] = BuildRequestUserInputAnswers(answers)
        };

        return "CodeIsland HUD answer: " + response.ToJsonString();
    }

    private static string BuildRequestUserInputDismissReason(string reason) =>
        "User dismissed request_user_input in CodeIsland HUD" +
        (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}");

    private static JsonObject BuildRequestUserInputAnswers(IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var answerObject = new JsonObject();
        foreach (var answer in answers)
        {
            answerObject[answer.Key] = new JsonObject
            {
                ["answers"] = new JsonArray(answer.Value.Select(static value => JsonValue.Create(value)).ToArray<JsonNode?>())
            };
        }

        return answerObject;
    }

    private static string BuildApprovalDecisionResponse(HookEvent evt, string behavior, string? reason, bool always)
    {
        if (GetHookEventName(evt) == "PreToolUse")
        {
            return behavior.Equals("deny", StringComparison.OrdinalIgnoreCase)
                ? BuildPreToolUseDenyResponse(reason ?? "User denied this operation")
                : "{}";
        }

        return BuildRequestUserInputDecisionResponse(evt, behavior, reason);
    }

    private static string BuildPreToolUseDenyResponse(string reason) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = string.IsNullOrWhiteSpace(reason) ? "Denied by CodeIsland HUD" : reason
            }
        }.ToJsonString();

    private static string BuildRequestUserInputDecisionResponse(HookEvent evt, string behavior, string? reason)
    {
        var decision = new JsonObject { ["behavior"] = behavior };
        if (!string.IsNullOrWhiteSpace(reason))
            decision["reason"] = reason;

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = GetHookEventName(evt),
                ["decision"] = decision
            }
        }.ToJsonString();
    }

    private static string GetHookEventName(HookEvent evt) =>
        EventNormalizer.NormalizeEventName(evt.Source ?? "unknown", evt.EventName) == "PreToolUse"
            ? "PreToolUse"
            : "PermissionRequest";
}
