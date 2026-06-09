using System.Text.Json;

namespace CodeIsland.Core.Services;

public static class HookResponseDiagnostics
{
    public static string GetResponseType(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "empty";

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "other";

            if (!root.EnumerateObject().Any())
                return "empty";

            if (!root.TryGetProperty("hookSpecificOutput", out var output) ||
                output.ValueKind != JsonValueKind.Object)
                return "other";

            if (output.TryGetProperty("permissionDecision", out _))
                return "permissionDecision";

            if (output.TryGetProperty("decision", out var decision) &&
                decision.ValueKind == JsonValueKind.Object)
            {
                return decision.TryGetProperty("updatedInput", out _)
                    ? "decision.updatedInput"
                    : "decision";
            }

            if (output.TryGetProperty("updatedInput", out _))
                return "updatedInput";

            return "hookSpecificOutput";
        }
        catch (JsonException)
        {
            return "invalid-json";
        }
    }
}
