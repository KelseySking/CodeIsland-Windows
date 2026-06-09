using System.Text.Json;
using CodeIsland.Core.Models;
using Xunit;

namespace CodeIsland.Core.Tests;

public class HookEventTests
{
    [Fact]
    public void FromJson_ParsesBasicFields()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "abc-123",
            ["tool_name"] = "Bash",
            ["tool_use_id"] = "tool-456"
        });

        using var doc = JsonDocument.Parse(json);
        var evt = HookEvent.FromJson(doc.RootElement);

        Assert.NotNull(evt);
        Assert.Equal("PreToolUse", evt.EventName);
        Assert.Equal("abc-123", evt.SessionId);
        Assert.Equal("Bash", evt.ToolName);
        Assert.Equal("tool-456", evt.ToolUseId);
    }

    [Fact]
    public void FromJson_AcceptsAlternateFieldNames()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hookEventName"] = "SessionStart",
            ["sessionId"] = "xyz-789",
            ["toolName"] = "Read"
        });

        using var doc = JsonDocument.Parse(json);
        var evt = HookEvent.FromJson(doc.RootElement);

        Assert.NotNull(evt);
        Assert.Equal("SessionStart", evt.EventName);
        Assert.Equal("xyz-789", evt.SessionId);
        Assert.Equal("Read", evt.ToolName);
    }

    [Fact]
    public void FromJson_ReturnsNull_WhenNoEventName()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = "abc"
        });

        using var doc = JsonDocument.Parse(json);
        var evt = HookEvent.FromJson(doc.RootElement);

        Assert.Null(evt);
    }

    [Fact]
    public void FromJson_InjectsSourceFromParameter()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop"
        });

        using var doc = JsonDocument.Parse(json);
        var evt = HookEvent.FromJson(doc.RootElement, "cursor");

        Assert.NotNull(evt);
        Assert.Equal("cursor", evt.Source);
    }

    [Fact]
    public void FromJson_ParsesBridgeMetadata()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["_source"] = "gemini",
            ["_ppid"] = 1234,
            ["_tracked_pid"] = 5678,
            ["_tracked_pid_kind"] = "shell",
            ["_tracked_process_started_at_utc"] = "2026-06-08T10:11:12.0000000Z"
        });

        using var doc = JsonDocument.Parse(json);
        var evt = HookEvent.FromJson(doc.RootElement);

        Assert.NotNull(evt);
        Assert.Equal("gemini", evt.Source);
        Assert.Equal(1234, evt.ParentPid);
        Assert.Equal(5678, evt.TrackedPid);
        Assert.Equal("shell", evt.TrackedPidKind);
        Assert.Equal(new DateTime(2026, 6, 8, 10, 11, 12, DateTimeKind.Utc), evt.TrackedProcessStartedAtUtc);
    }

    [Fact]
    public void FromJson_LeavesTrackedPidNullWhenMissingAndKeepsParentPid()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["_ppid"] = 1234
        });

        using var doc = JsonDocument.Parse(json);
        var evt = HookEvent.FromJson(doc.RootElement);

        Assert.NotNull(evt);
        Assert.Equal(1234, evt.ParentPid);
        Assert.Null(evt.TrackedPid);
        Assert.Null(evt.TrackedPidKind);
    }
}
