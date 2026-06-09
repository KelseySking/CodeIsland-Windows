using CodeIsland.Core.Services;
using Xunit;

namespace CodeIsland.Core.Tests;

public class EventNormalizerTests
{
    [Theory]
    [InlineData("cursor", "beforeSubmitPrompt", "UserPromptSubmit")]
    [InlineData("cursor", "beforeShellExecution", "PreToolUse")]
    [InlineData("cursor", "afterShellExecution", "PostToolUse")]
    [InlineData("gemini", "BeforeTool", "PreToolUse")]
    [InlineData("gemini", "AfterTool", "PostToolUse")]
    [InlineData("copilot", "sessionStart", "SessionStart")]
    [InlineData("copilot", "preToolUse", "PreToolUse")]
    [InlineData("cline", "TaskStart", "SessionStart")]
    [InlineData("cline", "TaskResume", "UserPromptSubmit")]
    [InlineData("traecli", "session_start", "SessionStart")]
    [InlineData("traecli", "pre_tool_use", "PreToolUse")]
    [InlineData("claude", "UserPromptSubmit", "UserPromptSubmit")]
    [InlineData("claude", "PreToolUse", "PreToolUse")]
    [InlineData("claude", "stop", "Stop")]
    [InlineData("claude", "STOP", "Stop")]
    [InlineData("claude", "sessionEnd", "SessionEnd")]
    [InlineData("claude", "sessionend", "SessionEnd")]
    [InlineData("claude", "permissionRequest", "PermissionRequest")]
    [InlineData("claude", "post_tool_use_failure", "PostToolUseFailure")]
    [InlineData("claude", "subagent_start", "SubagentStart")]
    [InlineData("claude", "subagent_stop", "SubagentStop")]
    [InlineData("claude", "pre_compact", "PreCompact")]
    [InlineData("claude", "post_compact", "PostCompact")]
    [InlineData("claude", "permission_denied", "PermissionDenied")]
    [InlineData("claude", "notification", "Notification")]
    [InlineData("unknown", "Stop", "Stop")]
    public void NormalizeEventName_ReturnsCorrectResult(string source, string raw, string expected)
    {
        var result = EventNormalizer.NormalizeEventName(source, raw);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeFieldName_EventNameVariants()
    {
        Assert.Equal("event_name", EventNormalizer.NormalizeFieldName("hook_event_name"));
        Assert.Equal("event_name", EventNormalizer.NormalizeFieldName("hookEventName"));
        Assert.Equal("event_name", EventNormalizer.NormalizeFieldName("event_name"));
    }

    [Fact]
    public void NormalizeFieldName_SessionIdVariants()
    {
        Assert.Equal("session_id", EventNormalizer.NormalizeFieldName("session_id"));
        Assert.Equal("session_id", EventNormalizer.NormalizeFieldName("sessionId"));
    }
}
