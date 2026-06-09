using System.Text;
using System.Text.Json;
using CodeIsland.Core.Models;

namespace CodeIsland.Core.Services;

public static class CodexPermissionRules
{
    private static readonly HashSet<string> NonPersistentCommandPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "cmd", "node", "pwsh", "powershell", "python", "python3", "sh"
    };

    public static string RulesFilePath => Path.Combine(ConfigInstaller.ResolveCodexHome(), "rules", "codeisland.rules");

    public static bool TryAppendAllowRule(PermissionRequest request)
    {
        if (!TryResolvePattern(request.ToolInput, out var pattern) || pattern.Count == 0)
            return false;

        var justification = TryGetString(request.ToolInput, "justification", "reason")
            ?? "User chose always allow in CodeIsland.";
        var line = FormatPrefixRule(pattern, justification);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RulesFilePath)!);
            var existing = File.Exists(RulesFilePath) ? File.ReadAllText(RulesFilePath) : string.Empty;
            if (existing.Contains(line, StringComparison.Ordinal))
                return true;

            var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : Environment.NewLine;
            var prefix = existing.Length == 0 || existing.EndsWith("\n", StringComparison.Ordinal) ? string.Empty : newline;
            File.WriteAllText(RulesFilePath, existing + prefix + line + newline);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatPrefixRule(IReadOnlyList<string> pattern, string justification)
    {
        var escapedPattern = string.Join(", ", pattern.Select(static item => $"\"{EscapeRuleString(item)}\""));
        return $"prefix_rule(pattern = [{escapedPattern}], decision = \"allow\", justification = \"{EscapeRuleString(justification)}\")";
    }

    private static bool TryResolvePattern(Dictionary<string, object?>? input, out IReadOnlyList<string> pattern)
    {
        pattern = [];
        if (input == null)
            return false;

        if (TryGetValue(input, out var prefixRule, "prefix_rule", "prefixRule") &&
            TryReadPrefixRule(prefixRule, out pattern))
        {
            return true;
        }

        if (TryGetString(input, "command") is { } command)
            return TryParseShellPrefix(command, out pattern);

        return false;
    }

    private static bool TryReadPrefixRule(object? value, out IReadOnlyList<string> pattern)
    {
        pattern = [];

        switch (value)
        {
            case JsonElement element:
                return TryReadPrefixRuleElement(element, out pattern);
            case IEnumerable<string> strings:
                pattern = strings.Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray();
                return pattern.Count > 0;
            case IEnumerable<object?> objects:
                pattern = objects.Select(static item => item?.ToString() ?? "")
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .ToArray();
                return pattern.Count > 0;
            case string text:
                return TryReadPrefixRuleString(text, out pattern);
            default:
                return false;
        }
    }

    private static bool TryReadPrefixRuleString(string text, out IReadOnlyList<string> pattern)
    {
        pattern = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return TryReadPrefixRuleElement(doc.RootElement, out pattern);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        pattern = [trimmed];
        return true;
    }

    private static bool TryReadPrefixRuleElement(JsonElement element, out IReadOnlyList<string> pattern)
    {
        pattern = [];
        if (element.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(element, "pattern", out var patternElement))
        {
            return TryReadPrefixRuleElement(patternElement, out pattern);
        }

        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
            if (!string.IsNullOrWhiteSpace(text))
                values.Add(text);
        }

        pattern = values;
        return values.Count > 0;
    }

    private static bool TryParseShellPrefix(string command, out IReadOnlyList<string> pattern)
    {
        pattern = [];
        var tokens = TokenizeShellPrefix(command);
        if (tokens.Count < 2)
            return false;

        if (NonPersistentCommandPrefixes.Contains(tokens[0]))
            return false;

        var count = tokens[0].Equals("npm", StringComparison.OrdinalIgnoreCase) &&
                    tokens.Count >= 3 &&
                    tokens[1].Equals("run", StringComparison.OrdinalIgnoreCase)
            ? 3
            : 2;

        pattern = tokens.Take(count).ToArray();
        return true;
    }

    private static List<string> TokenizeShellPrefix(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (quote == '\0')
            {
                if (char.IsWhiteSpace(ch))
                {
                    FlushToken();
                    if (tokens.Count >= 3)
                        break;
                    continue;
                }

                if (ch is '|' or ';' or '<' or '>')
                    break;

                if (ch == '&')
                    break;

                if (ch is '"' or '\'')
                {
                    quote = ch;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch == quote)
            {
                quote = '\0';
                continue;
            }

            current.Append(ch);
        }

        FlushToken();
        return tokens;

        void FlushToken()
        {
            if (current.Length == 0)
                return;

            tokens.Add(current.ToString());
            current.Clear();
        }
    }

    private static string? TryGetString(Dictionary<string, object?>? input, params string[] names)
    {
        if (input == null || !TryGetValue(input, out var value, names))
            return null;

        return value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => value?.ToString()
        };
    }

    private static bool TryGetValue(Dictionary<string, object?> input, out object? value, params string[] names)
    {
        foreach (var pair in input)
        {
            if (names.Any(name => string.Equals(name, pair.Key, StringComparison.OrdinalIgnoreCase)))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string EscapeRuleString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            _ = ch switch
            {
                '\\' => builder.Append(@"\\"),
                '"' => builder.Append("\\\""),
                '\r' => builder.Append(@"\r"),
                '\n' => builder.Append(@"\n"),
                '\t' => builder.Append(@"\t"),
                _ => builder.Append(ch)
            };
        }

        return builder.ToString();
    }
}
