using System.Text.Json;

namespace CodeIsland.Core.Services;

public static class TranscriptPathResolver
{
    public static string? ExtractTranscriptPath(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        var direct = GetStringField(obj, "transcript_path", "transcriptPath");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct.Trim();

        foreach (var nestKey in new[] { "payload", "data", "context", "workspace" })
        {
            if (obj.TryGetProperty(nestKey, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                var nestedPath = ExtractTranscriptPath(nested);
                if (!string.IsNullOrWhiteSpace(nestedPath))
                    return nestedPath;
            }
        }

        return null;
    }

    public static string? ExtractWorkingDirectory(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        var direct = GetStringField(obj,
            "cwd", "current_dir", "currentDir", "working_directory", "workingDirectory",
            "workspace", "workspaceFolder", "workspace_folder", "workspacePath", "workspace_path");
        if (!string.IsNullOrWhiteSpace(direct))
            return NormalizeDirectoryCandidate(direct);

        var transcriptPath = ExtractTranscriptPath(obj);
        if (!string.IsNullOrWhiteSpace(transcriptPath))
            return NormalizeDirectoryCandidate(GetProjectDirectoryFromTranscriptPath(transcriptPath));

        foreach (var nestKey in new[] { "payload", "data", "context", "workspace" })
        {
            if (obj.TryGetProperty(nestKey, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                var nestedDirectory = ExtractWorkingDirectory(nested);
                if (!string.IsNullOrWhiteSpace(nestedDirectory))
                    return nestedDirectory;
            }
        }

        return null;
    }

    public static string? TryResolveCodexTranscriptPath(string? sessionId)
    {
        var normalizedSessionId = sessionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId) ||
            normalizedSessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        var sessionsDirectory = Path.Combine(ConfigInstaller.ResolveCodexHome(), "sessions");
        if (!Directory.Exists(sessionsDirectory))
            return null;

        try
        {
            string? bestPath = null;
            var bestTimestamp = DateTime.MinValue;

            foreach (var candidate in Directory.EnumerateFiles(
                         sessionsDirectory,
                         $"*{normalizedSessionId}.jsonl",
                         SearchOption.AllDirectories))
            {
                if (!IsCodexTranscriptFileForSession(candidate, normalizedSessionId))
                    continue;

                var timestamp = SafeGetLastWriteTimeUtc(candidate);
                if (bestPath == null || timestamp >= bestTimestamp)
                {
                    bestPath = candidate;
                    bestTimestamp = timestamp;
                }
            }

            return bestPath;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? GetProjectDirectoryFromTranscriptPath(string transcriptPath)
    {
        var directory = Path.GetDirectoryName(transcriptPath) ?? transcriptPath;
        var segments = SplitPathSegments(directory).ToArray();
        var claudeIndex = Array.FindLastIndex(segments,
            static segment => segment.Equals(".claude", StringComparison.OrdinalIgnoreCase));

        if (claudeIndex >= 0)
        {
            if (claudeIndex + 2 < segments.Length &&
                segments[claudeIndex + 1].Equals("projects", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeClaudeTranscriptProjectSegment(segments[claudeIndex + 2]) ?? directory;
            }

            if (claudeIndex == segments.Length - 1)
                return Path.GetDirectoryName(directory) ?? directory;
        }

        return directory;
    }

    public static string? ExtractProjectName(string? path)
    {
        var trimmed = path?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        return SplitPathSegments(trimmed).LastOrDefault();
    }

    public static IEnumerable<string> SplitPathSegments(string path) =>
        path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NormalizeDirectoryCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsCodexTranscriptFileForSession(string path, string sessionId)
    {
        if (!Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            return false;

        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith(sessionId, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? DecodeClaudeTranscriptProjectSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return null;

        var normalized = segment.Trim();
        if (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == '-' && normalized[2] == '-')
        {
            var drive = char.ToUpperInvariant(normalized[0]);
            var rest = normalized[3..].Replace('-', Path.DirectorySeparatorChar);
            return $"{drive}:{Path.DirectorySeparatorChar}{rest}";
        }

        if (normalized.Length >= 4 && normalized[0] == '-' && char.IsLetter(normalized[1]) && normalized[2] == '-')
        {
            var drive = char.ToUpperInvariant(normalized[1]);
            var rest = normalized[3..].Replace('-', Path.DirectorySeparatorChar);
            return $"{drive}:{Path.DirectorySeparatorChar}{rest}";
        }

        if (normalized.StartsWith('-'))
            return Path.DirectorySeparatorChar + normalized.TrimStart('-').Replace('-', Path.DirectorySeparatorChar);

        return normalized.Replace('-', Path.DirectorySeparatorChar);
    }

    private static string? GetStringField(JsonElement json, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (json.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }

        return null;
    }
}
