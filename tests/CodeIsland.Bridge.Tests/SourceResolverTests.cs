using System.Text.Json;
using Xunit;

namespace CodeIsland.Bridge.Tests;

public class SourceResolverTests
{
    [Fact]
    public void InferSource_ExplicitSource_ReturnsExplicit()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "cmd.exe", @"C:\Windows\System32\cmd.exe"),
        };

        var result = SourceResolver.InferSource(ancestry, "gemini");
        Assert.Equal("gemini", result);
    }

    [Fact]
    public void InferSource_KnownExe_ReturnsSource()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "claude.exe", @"C:\Users\test\.claude\claude.exe"),
        };

        var result = SourceResolver.InferSource(ancestry);
        Assert.Equal("claude", result);
    }

    [Fact]
    public void InferSource_CursorExe_ReturnsCursor()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "Cursor.exe", @"C:\Users\test\AppData\Local\Programs\cursor\Cursor.exe"),
        };

        var result = SourceResolver.InferSource(ancestry);
        Assert.Equal("cursor", result);
    }

    [Fact]
    public void InferSource_UnknownExe_ReturnsUnknown()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "notepad.exe", @"C:\Windows\System32\notepad.exe"),
        };

        var result = SourceResolver.InferSource(ancestry);
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void InferSource_EmptyAncestry_ReturnsUnknown()
    {
        var result = SourceResolver.InferSource(new List<ProcessInfo>());
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void InferSource_PayloadSourceFallback_ReturnsPayloadSource()
    {
        using var doc = JsonDocument.Parse("""
            {
              "hook_event_name": "SessionStart",
              "env": {
                "CODEISLAND_SOURCE": "claude"
              }
            }
            """);

        var result = SourceResolver.InferSource(new List<ProcessInfo>(), payload: doc.RootElement);

        Assert.Equal("claude", result);
    }

    [Fact]
    public void InferSource_ClaudeTranscriptPathFallback_ReturnsClaude()
    {
        using var doc = JsonDocument.Parse("""
            {
              "hook_event_name": "SessionStart",
              "transcript_path": "C:\\Users\\test\\.claude\\projects\\-D-Work-my-project\\transcript.jsonl"
            }
            """);

        var result = SourceResolver.InferSource(new List<ProcessInfo>(), payload: doc.RootElement);

        Assert.Equal("claude", result);
    }

    [Fact]
    public void InferSource_NodeWrapperFallsBackToPayload()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "node.exe", @"C:\\Program Files\\nodejs\\node.exe"),
        };
        using var doc = JsonDocument.Parse("""
            {
              "hook_event_name": "SessionStart",
              "transcript_path": "C:\\Users\\test\\.claude\\projects\\-D-Work-my-project\\transcript.jsonl"
            }
            """);

        var result = SourceResolver.InferSource(ancestry, payload: doc.RootElement);

        Assert.Equal("claude", result);
    }
}
