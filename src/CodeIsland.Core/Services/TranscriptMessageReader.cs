using System.Text.Json;
using CodeIsland.Core.Models;

namespace CodeIsland.Core.Services;

public static class TranscriptMessageReader
{
    public static TranscriptReadResult ReadNewMessages(string transcriptPath, long startPosition)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
            return new TranscriptReadResult([], startPosition);

        try
        {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var safeStart = startPosition > 0 && startPosition <= stream.Length ? startPosition : 0;
            stream.Seek(safeStart, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            var messages = new List<ChatMessage>();
            while (reader.ReadLine() is { } line)
            {
                var message = TryParseLine(line);
                if (message != null)
                    messages.Add(message);
            }

            return new TranscriptReadResult(messages, stream.Position);
        }
        catch (IOException)
        {
            return new TranscriptReadResult([], startPosition);
        }
        catch (UnauthorizedAccessException)
        {
            return new TranscriptReadResult([], startPosition);
        }
    }

    private static ChatMessage? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var codexMessage = TryParseCodexResponseItem(root);
            if (codexMessage != null)
                return codexMessage;

            var role = GetString(root, "role") ?? GetString(root, "type") ?? GetNestedString(root, "message", "role");
            var isUser = role?.Equals("user", StringComparison.OrdinalIgnoreCase) == true;
            var isAssistant = role?.Equals("assistant", StringComparison.OrdinalIgnoreCase) == true;
            if (!isUser && !isAssistant)
                return null;

            var text = ExtractText(root);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return new ChatMessage
            {
                IsUser = isUser,
                Text = text,
                Timestamp = ExtractTimestamp(root) ?? DateTime.UtcNow
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ChatMessage? TryParseCodexResponseItem(JsonElement root)
    {
        if (!string.Equals(GetString(root, "type"), "response_item", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;

        if (!string.Equals(GetString(payload, "type"), "message", StringComparison.OrdinalIgnoreCase))
            return null;

        var role = GetString(payload, "role");
        var isUser = role?.Equals("user", StringComparison.OrdinalIgnoreCase) == true;
        var isAssistant = role?.Equals("assistant", StringComparison.OrdinalIgnoreCase) == true;
        if (!isUser && !isAssistant)
            return null;

        var text = ExtractCodexMessageText(payload);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return new ChatMessage
        {
            IsUser = isUser,
            Text = text,
            Timestamp = ExtractTimestamp(root) ?? ExtractTimestamp(payload) ?? DateTime.UtcNow
        };
    }

    private static string? ExtractText(JsonElement root)
    {
        if (TryExtractText(root, out var text))
            return text;

        if (root.TryGetProperty("message", out var message) && TryExtractText(message, out text))
            return text;

        return null;
    }

    private static bool TryExtractText(JsonElement element, out string? text)
    {
        text = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var key in new[] { "text", "content", "message", "summary" })
        {
            if (!element.TryGetProperty(key, out var value))
                continue;

            text = ExtractTextValue(value);
            if (!string.IsNullOrWhiteSpace(text))
                return true;
        }

        return false;
    }

    private static string? ExtractCodexMessageText(JsonElement payload)
    {
        return payload.TryGetProperty("content", out var content)
            ? ExtractCodexContentText(content)
            : null;
    }

    private static string? ExtractCodexContentText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                var part = ExtractCodexContentText(item);
                if (!string.IsNullOrWhiteSpace(part))
                    parts.Add(part);
            }

            return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
        }

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        var blockType = GetString(value, "type");
        if (!string.IsNullOrWhiteSpace(blockType) && !IsCodexVisibleTextBlock(blockType))
            return null;

        var candidateKeys = !string.IsNullOrWhiteSpace(blockType)
            ? new[] { "text", "input_text", "output_text", "content" }
            : new[] { "input_text", "output_text", "text" };

        foreach (var key in candidateKeys)
        {
            if (!value.TryGetProperty(key, out var nested))
                continue;

            var text = ExtractCodexContentText(nested);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static bool IsCodexVisibleTextBlock(string blockType) =>
        blockType.Equals("input_text", StringComparison.OrdinalIgnoreCase) ||
        blockType.Equals("output_text", StringComparison.OrdinalIgnoreCase) ||
        blockType.Equals("text", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractTextValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                var part = ExtractTextValue(item);
                if (!string.IsNullOrWhiteSpace(part))
                    parts.Add(part);
            }

            return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out var typeProp) &&
                typeProp.ValueKind == JsonValueKind.String &&
                typeProp.GetString() is { } blockType &&
                !blockType.Equals("text", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var key in new[] { "text", "content", "message", "summary" })
            {
                if (value.TryGetProperty(key, out var nested))
                {
                    var text = ExtractTextValue(nested);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
        }

        return null;
    }

    private static DateTime? ExtractTimestamp(JsonElement root)
    {
        var timestamp = GetString(root, "timestamp", "created_at", "createdAt");
        return DateTime.TryParse(timestamp, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static string? GetString(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static string? GetNestedString(JsonElement element, string objectKey, string stringKey)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(objectKey, out var nested) &&
               nested.ValueKind == JsonValueKind.Object &&
               nested.TryGetProperty(stringKey, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

public sealed record TranscriptReadResult(IReadOnlyList<ChatMessage> Messages, long Position);
