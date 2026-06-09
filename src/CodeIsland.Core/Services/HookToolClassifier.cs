using System.Text.Json;
using CodeIsland.Core.Models;

namespace CodeIsland.Core.Services;

public enum HookQuestionToolKind
{
    None,
    AskUserQuestion,
    CodexRequestUserInput
}

public static class HookToolClassifier
{
    private static readonly string[] ToolNameKeys =
    [
        "tool_name",
        "toolName",
        "tool",
        "name",
        "function_name",
        "functionName"
    ];

    private static readonly string[] NestedObjectKeys =
    [
        "tool",
        "function",
        "payload",
        "data",
        "request",
        "env",
        "environment"
    ];

    public static string? GetToolName(HookEvent evt) =>
        FirstNonBlank(evt.ToolName) ??
        GetToolName(evt.RawJson);

    public static string? GetToolName(JsonElement? element) =>
        element is { } value ? GetToolName(value, depth: 0) : null;

    public static string? GetToolName(IReadOnlyDictionary<string, object?> payload) =>
        GetToolName(payload, depth: 0);

    public static HookQuestionToolKind GetQuestionToolKind(HookEvent evt) =>
        GetQuestionToolKind(GetToolName(evt));

    public static HookQuestionToolKind GetQuestionToolKind(IReadOnlyDictionary<string, object?> payload) =>
        GetQuestionToolKind(GetToolName(payload));

    public static HookQuestionToolKind GetQuestionToolKind(string? toolName)
    {
        if (toolName?.Equals("AskUserQuestion", StringComparison.OrdinalIgnoreCase) == true)
            return HookQuestionToolKind.AskUserQuestion;

        if (toolName is not null &&
            (toolName.Equals("request_user_input", StringComparison.OrdinalIgnoreCase) ||
             toolName.Equals("functions.request_user_input", StringComparison.OrdinalIgnoreCase)))
            return HookQuestionToolKind.CodexRequestUserInput;

        return HookQuestionToolKind.None;
    }

    public static bool IsAskUserQuestion(HookEvent evt) =>
        GetQuestionToolKind(evt) == HookQuestionToolKind.AskUserQuestion;

    public static bool IsCodexRequestUserInput(HookEvent evt) =>
        GetQuestionToolKind(evt) == HookQuestionToolKind.CodexRequestUserInput;

    public static bool IsQuestionTool(HookEvent evt) =>
        GetQuestionToolKind(evt) != HookQuestionToolKind.None;

    public static bool IsQuestionTool(IReadOnlyDictionary<string, object?> payload) =>
        GetQuestionToolKind(payload) != HookQuestionToolKind.None;

    public static bool ShouldBlockQuestionTool(HookEvent evt, string normalizedEventName)
    {
        var kind = GetQuestionToolKind(evt);
        return ShouldBlockQuestionTool(kind, normalizedEventName);
    }

    public static bool ShouldBlockQuestionTool(IReadOnlyDictionary<string, object?> payload, string normalizedEventName)
    {
        var kind = GetQuestionToolKind(payload);
        return ShouldBlockQuestionTool(kind, normalizedEventName);
    }

    private static bool ShouldBlockQuestionTool(HookQuestionToolKind kind, string normalizedEventName)
    {
        if (kind == HookQuestionToolKind.None)
            return false;

        if (kind == HookQuestionToolKind.CodexRequestUserInput)
            return normalizedEventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static string? GetToolName(JsonElement element, int depth)
    {
        if (depth > 8)
            return null;

        if (element.ValueKind == JsonValueKind.String)
            return FirstNonBlank(element.GetString());

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var fromItem = GetToolName(item, depth + 1);
                if (!string.IsNullOrWhiteSpace(fromItem))
                    return fromItem;
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in ToolNameKeys)
        {
            if (!TryGetPropertyIgnoreCase(element, key, out var prop))
                continue;

            if (prop.ValueKind == JsonValueKind.String)
                return FirstNonBlank(prop.GetString());

            if (prop.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var nested = GetToolName(prop, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        foreach (var key in NestedObjectKeys)
        {
            if (TryGetPropertyIgnoreCase(element, key, out var nested) &&
                nested.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var result = GetToolName(nested, depth + 1);
                if (!string.IsNullOrWhiteSpace(result))
                    return result;
            }
        }

        return null;
    }

    private static string? GetToolName(IReadOnlyDictionary<string, object?> payload, int depth)
    {
        if (depth > 8)
            return null;

        foreach (var key in ToolNameKeys)
        {
            if (!TryGetValueIgnoreCase(payload, key, out var value))
                continue;

            var fromValue = GetToolName(value, depth + 1);
            if (!string.IsNullOrWhiteSpace(fromValue))
                return fromValue;
        }

        foreach (var key in NestedObjectKeys)
        {
            if (!TryGetValueIgnoreCase(payload, key, out var value))
                continue;

            if (!IsSearchableContainer(value))
                continue;

            var fromValue = GetToolName(value, depth + 1);
            if (!string.IsNullOrWhiteSpace(fromValue))
                return fromValue;
        }

        return null;
    }

    private static string? GetToolName(object? value, int depth) => value switch
    {
        null => null,
        string text => FirstNonBlank(text),
        JsonElement element => GetToolName(element, depth),
        IReadOnlyDictionary<string, object?> dict => GetToolName(dict, depth),
        IDictionary<string, object?> dict => GetToolName(new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase), depth),
        _ => null
    };

    private static bool IsSearchableContainer(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array } => true,
        IReadOnlyDictionary<string, object?> => true,
        IDictionary<string, object?> => true,
        _ => false
    };

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string key, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetValueIgnoreCase(IReadOnlyDictionary<string, object?> payload, string key, out object? value)
    {
        foreach (var pair in payload)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? FirstNonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
