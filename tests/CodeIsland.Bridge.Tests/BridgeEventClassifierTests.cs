using System.Text.Json;
using CodeIsland.Bridge;
using Xunit;

namespace CodeIsland.Bridge.Tests;

public class BridgeEventClassifierTests
{
    [Fact]
    public void PermissionRequest_IsBlocking()
    {
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["_source"] = "claude"
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void PermissionDenied_IsNotBlocking()
    {
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionDenied",
            ["_source"] = "claude"
        };

        Assert.False(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void OrdinaryPreToolUse_IsNotBlocking()
    {
        using var doc = JsonDocument.Parse("""
            { "command": "dotnet build" }
            """);
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["_source"] = "claude",
            ["tool_input"] = doc.RootElement.Clone()
        };

        Assert.False(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void PreToolUse_WithExplicitApprovalSignal_IsBlocking()
    {
        using var doc = JsonDocument.Parse("""
            { "command": "rm -rf temp", "approval_required": true }
            """);
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["_source"] = "claude",
            ["tool_input"] = doc.RootElement.Clone()
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void AskUserQuestionPermissionRequest_IsBlocking()
    {
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["_source"] = "claude",
            ["tool_name"] = "AskUserQuestion"
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void AskUserQuestionPreToolUse_IsBlockingWithoutApprovalSignal()
    {
        using var doc = JsonDocument.Parse("""
            { "questions": [{ "question": "采用哪个方案？" }] }
            """);
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["_source"] = "claude",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = doc.RootElement.Clone()
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Theory]
    [InlineData("request_user_input")]
    [InlineData("functions.request_user_input")]
    public void CodexRequestUserInputPreToolUse_IsBlockingWithoutApprovalSignal(string toolName)
    {
        using var doc = JsonDocument.Parse("""
            { "questions": [{ "id": "next", "question": "Next step?" }] }
            """);
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["_source"] = "codex",
            ["tool_name"] = toolName,
            ["tool_input"] = doc.RootElement.Clone()
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Theory]
    [InlineData("request_user_input")]
    [InlineData("functions.request_user_input")]
    public void CodexRequestUserInputPermissionRequest_IsBlocking(string toolName)
    {
        using var doc = JsonDocument.Parse("""
            { "questions": [{ "id": "next", "question": "Next step?" }] }
            """);
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["_source"] = "codex",
            ["tool_name"] = toolName,
            ["tool_input"] = doc.RootElement.Clone()
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void CodexRequestUserInputPermissionRequest_WithNestedFunctionName_IsBlocking()
    {
        using var doc = JsonDocument.Parse("""
            { "name": "functions.request_user_input" }
            """);
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["_source"] = "codex",
            ["function"] = doc.RootElement.Clone()
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void CodexRequestUserInputPreToolUse_WithNestedFunctionName_IsBlocking()
    {
        using var doc = JsonDocument.Parse("""
            { "name": "functions.request_user_input" }
            """);
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["_source"] = "codex",
            ["function"] = doc.RootElement.Clone()
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void CodexRequestUserInputPreToolUse_WithApprovalSignal_IsBlocking()
    {
        using var doc = JsonDocument.Parse("""
            { "questions": [{ "id": "next", "question": "Next step?" }], "approval_required": true }
            """);
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["_source"] = "codex",
            ["tool_name"] = "request_user_input",
            ["tool_input"] = doc.RootElement.Clone()
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void AskUserQuestionPostToolUse_IsNotBlockingWithoutQuestionPayload()
    {
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PostToolUse",
            ["_source"] = "claude",
            ["tool_name"] = "AskUserQuestion"
        };

        Assert.False(BridgeEventClassifier.IsBlockingEvent(payload));
    }

    [Fact]
    public void Notification_WithQuestionPayload_IsBlocking()
    {
        using var doc = JsonDocument.Parse("""
            { "question": "继续执行吗？" }
            """);
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Notification",
            ["_source"] = "claude",
            ["tool_input"] = doc.RootElement.Clone()
        };

        Assert.True(BridgeEventClassifier.IsBlockingEvent(payload));
    }
}
