using System.Text.Json;
using Xunit;

namespace CodeIsland.Bridge.Tests;

public class BridgePayloadSerializerTests
{
    [Fact]
    public void Serialize_WritesJsonElementAndPrimitivePayloadWithoutReflectionSerializer()
    {
        using var document = JsonDocument.Parse("""
        {
          "hook_event_name": "SessionStart",
          "session_id": "manual-test",
          "tool_input": {
            "command": "dotnet test",
            "questions": ["continue?"]
          },
          "allowed": true,
          "attempt": 2,
          "optional": null
        }
        """);

        var root = document.RootElement;
        var payload = new Dictionary<string, object?>
        {
            ["hook_event_name"] = root.GetProperty("hook_event_name").GetString(),
            ["session_id"] = root.GetProperty("session_id").GetString(),
            ["tool_input"] = root.GetProperty("tool_input").Clone(),
            ["allowed"] = root.GetProperty("allowed").Clone(),
            ["attempt"] = 2,
            ["signed_small"] = (sbyte)-3,
            ["unsigned_small"] = (ushort)7,
            ["ratio"] = 1.25d,
            ["cost"] = 12.34m,
            ["optional"] = null,
            ["_source"] = "claude",
            ["_ppid"] = 123,
            ["_hook_ppid"] = 456,
            ["_tracked_pid"] = 789,
            ["_tracked_pid_kind"] = "shell",
            ["_tracked_process_started_at_utc"] = "2026-06-08T10:11:12.0000000Z",
        };

        var json = BridgePayloadSerializer.Serialize(payload);

        using var serialized = JsonDocument.Parse(json);
        var serializedRoot = serialized.RootElement;
        Assert.Equal("SessionStart", serializedRoot.GetProperty("hook_event_name").GetString());
        Assert.Equal("manual-test", serializedRoot.GetProperty("session_id").GetString());
        Assert.Equal("dotnet test", serializedRoot.GetProperty("tool_input").GetProperty("command").GetString());
        Assert.Equal("continue?", serializedRoot.GetProperty("tool_input").GetProperty("questions")[0].GetString());
        Assert.True(serializedRoot.GetProperty("allowed").GetBoolean());
        Assert.Equal(2, serializedRoot.GetProperty("attempt").GetInt32());
        Assert.Equal(-3, serializedRoot.GetProperty("signed_small").GetSByte());
        Assert.Equal(7, serializedRoot.GetProperty("unsigned_small").GetUInt16());
        Assert.Equal(1.25d, serializedRoot.GetProperty("ratio").GetDouble());
        Assert.Equal(12.34m, serializedRoot.GetProperty("cost").GetDecimal());
        Assert.Equal(JsonValueKind.Null, serializedRoot.GetProperty("optional").ValueKind);
        Assert.Equal("claude", serializedRoot.GetProperty("_source").GetString());
        Assert.Equal(123, serializedRoot.GetProperty("_ppid").GetInt32());
        Assert.Equal(456, serializedRoot.GetProperty("_hook_ppid").GetInt32());
        Assert.Equal(789, serializedRoot.GetProperty("_tracked_pid").GetInt32());
        Assert.Equal("shell", serializedRoot.GetProperty("_tracked_pid_kind").GetString());
        Assert.Equal("2026-06-08T10:11:12.0000000Z", serializedRoot.GetProperty("_tracked_process_started_at_utc").GetString());
    }
}
