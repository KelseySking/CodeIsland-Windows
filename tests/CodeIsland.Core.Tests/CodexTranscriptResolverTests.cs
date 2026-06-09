using System.Text.Json;
using CodeIsland.Core.Models;
using CodeIsland.Core.Services;
using Xunit;

namespace CodeIsland.Core.Tests;

public sealed class CodexTranscriptResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalUserProfileOverride;
    private readonly string? _originalCodexHome;

    public CodexTranscriptResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codeisland-codex-transcript-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalUserProfileOverride = Environment.GetEnvironmentVariable("CODEISLAND_TEST_USERPROFILE");
        _originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEISLAND_TEST_USERPROFILE", _tempDir);
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(_tempDir, "custom-codex"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEISLAND_TEST_USERPROFILE", _originalUserProfileOverride);
        Environment.SetEnvironmentVariable("CODEX_HOME", _originalCodexHome);

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }

    [Fact]
    public void TryResolveCodexTranscriptPath_UsesCodexHomeSessionsDirectory()
    {
        var path = WriteCodexTranscript("codex-session-123");

        var resolved = TranscriptPathResolver.TryResolveCodexTranscriptPath("codex-session-123");

        Assert.Equal(path, resolved);
    }

    [Fact]
    public void TryResolveCodexTranscriptPath_BlankCodexHomeFallsBackToDotCodex()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", "   ");
        var path = WriteCodexTranscript("blank-home-session");

        var resolved = TranscriptPathResolver.TryResolveCodexTranscriptPath("blank-home-session");

        Assert.Equal(path, resolved);
        Assert.StartsWith(Path.Combine(_tempDir, ".codex"), resolved);
    }

    [Fact]
    public void SessionStart_CodexResolvesTranscriptPathFromSessionId()
    {
        var path = WriteCodexTranscript("codex-session-456");
        var evt = MakeEvent("SessionStart", "codex-session-456", "codex");

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(path, state.TranscriptPath);
    }

    [Fact]
    public void SessionStart_ClaudeDoesNotResolveCodexTranscriptPathFromSessionId()
    {
        WriteCodexTranscript("claude-session-should-not-match");
        var evt = MakeEvent("SessionStart", "claude-session-should-not-match", "claude");

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Null(state.TranscriptPath);
    }

    [Fact]
    public void CodexSessionFlow_ResolvedTranscriptCanUpdateLastAssistantMessage()
    {
        const string sessionId = "flow-session";
        var path = WriteCodexTranscript(sessionId, """
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"How is the HUD?"}]}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"The HUD is live."}]}}
            """);
        var started = MakeEvent("SessionStart", sessionId, "codex");
        var (startState, _) = SessionSnapshot.ReduceEvent(null, started);
        var prompt = MakeEvent("UserPromptSubmit", sessionId, "codex", "How is the HUD?");
        var (processing, _) = SessionSnapshot.ReduceEvent(startState, prompt);

        var result = TranscriptMessageReader.ReadNewMessages(processing.TranscriptPath!, processing.TranscriptPosition);
        var clone = processing.Clone();
        clone.TranscriptPosition = result.Position;
        foreach (var message in result.Messages)
            SessionSnapshot.AddRecentMessage(clone, message);

        Assert.Equal(path, processing.TranscriptPath);
        Assert.Equal("How is the HUD?", clone.LastUserPrompt);
        Assert.Equal("The HUD is live.", clone.LastAssistantMessage);
    }

    private static HookEvent MakeEvent(string eventName, string sessionId, string source, string? prompt = null)
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = eventName,
            ["session_id"] = sessionId
        };

        if (!string.IsNullOrWhiteSpace(prompt))
            json["prompt"] = prompt;

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(json));
        return HookEvent.FromJson(document.RootElement, source)!;
    }

    private static string WriteCodexTranscript(string sessionId, string? content = null)
    {
        var directory = Path.Combine(ConfigInstaller.ResolveCodexHome(), "sessions", "2026", "06", "06");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"rollout-2026-06-06T000000-0000-{sessionId}.jsonl");
        File.WriteAllText(path, content?.Replace("\r\n", "\n") ?? string.Empty);
        return path;
    }
}
