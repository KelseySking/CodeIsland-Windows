using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIsland.Core.Models;

namespace CodeIsland.Core.Services;

internal static class ClaudeStyleHookResponseBuilder
{
    public static string BuildPermissionAllowResponse(HookEvent evt, bool always)
    {
        var normalized = EventNormalizer.NormalizeEventName(evt.Source ?? "unknown", evt.EventName);
        if (normalized == "PreToolUse")
        {
            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "allow",
                    ["permissionDecisionReason"] = always ? "User chose always allow" : "User allowed this operation"
                }
            }.ToJsonString();
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = new JsonObject { ["behavior"] = "allow" }
            }
        }.ToJsonString();
    }

    public static string BuildPermissionDenyResponse(HookEvent evt, string reason)
    {
        var normalized = EventNormalizer.NormalizeEventName(evt.Source ?? "unknown", evt.EventName);
        if (normalized == "PreToolUse")
        {
            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = reason
                }
            }.ToJsonString();
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = new JsonObject { ["behavior"] = "deny", ["reason"] = reason }
            }
        }.ToJsonString();
    }

    public static string BuildAskUserQuestionAnswerResponse(
        HookEvent evt,
        QuestionData question,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var hookName = EventNormalizer.NormalizeEventName(evt.Source ?? "unknown", evt.EventName);
        var updatedInput = CopyOriginalQuestionInput(question);
        var answerObject = new JsonObject();
        foreach (var answer in answers)
            answerObject[answer.Key] = JoinAnswers(answer.Value);
        updatedInput["answers"] = answerObject;

        var hookSpecificOutput = new JsonObject
        {
            ["hookEventName"] = hookName == "PermissionRequest" ? "PermissionRequest" : "PreToolUse",
            ["updatedInput"] = updatedInput
        };

        if (hookName == "PermissionRequest")
        {
            hookSpecificOutput["decision"] = new JsonObject { ["behavior"] = "allow" };
        }
        else
        {
            hookSpecificOutput["permissionDecision"] = "allow";
            hookSpecificOutput["permissionDecisionReason"] = "User answered the question";
        }

        return new JsonObject { ["hookSpecificOutput"] = hookSpecificOutput }.ToJsonString();
    }

    private static JsonObject CopyOriginalQuestionInput(QuestionData question)
    {
        if (question.OriginalInput is { } input && input.ValueKind == JsonValueKind.Object)
            return JsonNode.Parse(input.GetRawText()) as JsonObject ?? new JsonObject();

        return new JsonObject();
    }

    private static string JoinAnswers(IReadOnlyList<string> answers) => string.Join(", ", answers);
}
