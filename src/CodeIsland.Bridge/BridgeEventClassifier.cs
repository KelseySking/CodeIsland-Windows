using System.Text.Json;
using CodeIsland.Core.Services;

namespace CodeIsland.Bridge;

/// <summary>
/// Classifies bridge payloads that must wait for an app response.
/// </summary>
public static class BridgeEventClassifier
{
    public static bool IsBlockingEvent(IReadOnlyDictionary<string, object?> payload)
    {
        var eventName = GetStringValue(payload, "hook_event_name", "event_name", "event", "hookEventName", "eventName") ?? "";
        var source = GetStringValue(payload, "_source", "source") ?? "unknown";
        var normalizedName = EventNormalizer.NormalizeEventName(source, eventName);

        if (normalizedName == "PermissionRequest")
            return true;

        if (normalizedName == "PreToolUse" && HookToolClassifier.ShouldBlockQuestionTool(payload, normalizedName))
            return true;

        if (normalizedName == "PreToolUse" && HasApprovalNeededSignal(payload))
            return true;

        if ((normalizedName == "Notification" || normalizedName.Contains("Question", StringComparison.OrdinalIgnoreCase)) &&
            HasQuestionPayload(payload))
            return true;

        return false;
    }

    private static string? GetStringValue(IReadOnlyDictionary<string, object?> payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload.TryGetValue(key, out var value) && value != null)
                return value.ToString();
        }

        return null;
    }

    private static bool HasQuestionPayload(IReadOnlyDictionary<string, object?> payload)
    {
        return PayloadContainsQuestion(payload.GetValueOrDefault("tool_input"))
            || PayloadContainsQuestion(payload.GetValueOrDefault("toolInput"))
            || PayloadContainsQuestion(payload.GetValueOrDefault("input"))
            || payload.ContainsKey("question")
            || payload.ContainsKey("questions");
    }

    private static bool HasApprovalNeededSignal(IReadOnlyDictionary<string, object?> payload)
    {
        return PayloadContainsApprovalNeededSignal(payload.GetValueOrDefault("tool_input"))
            || PayloadContainsApprovalNeededSignal(payload.GetValueOrDefault("toolInput"))
            || PayloadContainsApprovalNeededSignal(payload.GetValueOrDefault("input"))
            || payload.Any(kvp => IsApprovalSignalName(kvp.Key) && IsTruthyApprovalSignal(kvp.Value));
    }

    private static bool PayloadContainsApprovalNeededSignal(object? value)
    {
        if (value is JsonElement element)
            return ElementContainsApprovalNeededSignal(element);

        return value is IReadOnlyDictionary<string, object?> dict &&
               DictionaryContainsApprovalNeededSignal(dict);
    }

    private static bool DictionaryContainsApprovalNeededSignal(IReadOnlyDictionary<string, object?> dict)
    {
        foreach (var kvp in dict)
        {
            if (IsApprovalSignalName(kvp.Key) && IsTruthyApprovalSignal(kvp.Value))
                return true;
            if (PayloadContainsApprovalNeededSignal(kvp.Value))
                return true;
        }

        return false;
    }

    private static bool ElementContainsApprovalNeededSignal(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var prop in element.EnumerateObject())
        {
            if (IsApprovalSignalName(prop.Name) && IsTruthyApprovalSignal(prop.Value))
                return true;
            if (prop.Value.ValueKind == JsonValueKind.Object && ElementContainsApprovalNeededSignal(prop.Value))
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

    private static bool IsTruthyApprovalSignal(object? value) => value switch
    {
        JsonElement element => IsTruthyApprovalSignalElement(element),
        bool boolValue => boolValue,
        string text => !text.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                       !text.Equals("0", StringComparison.OrdinalIgnoreCase),
        null => false,
        _ => true
    };

    private static bool IsTruthyApprovalSignalElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => value.GetString() is { } text &&
                                !text.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                                !text.Equals("0", StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Object => true,
        _ => false
    };

    private static bool PayloadContainsQuestion(object? value)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                return element.TryGetProperty("question", out _) || element.TryGetProperty("questions", out _);
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
                return text.Contains("question", StringComparison.OrdinalIgnoreCase);
        }

        if (value is IReadOnlyDictionary<string, object?> dict)
            return dict.ContainsKey("question") || dict.ContainsKey("questions");

        return value is string s && s.Contains("question", StringComparison.OrdinalIgnoreCase);
    }
}
