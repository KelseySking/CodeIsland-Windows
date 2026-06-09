using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIsland.Core.Models;
using CodeIsland.Core.Services;
using Xunit;

namespace CodeIsland.Core.Tests;

public class HookResponseBuilderTests
{
    [Fact]
    public void BuildQuestionAnswerResponse_CodexRequestUserInputPreToolUse_DeniesToolCallWithAnswerJsonInReason()
    {
        var evt = MakeEvent("PreToolUse", "codex", "functions.request_user_input", new
        {
            questions = new object[]
            {
                new { id = "approach", question = "Which approach?" },
                new { id = "checks", question = "Which checks?", multiple = true }
            }
        });
        var question = ExtractQuestion(evt);

        var response = HookResponseBuilder.BuildQuestionAnswerResponse(evt, question, new Dictionary<string, IReadOnlyList<string>>
        {
            ["approach"] = ["safe"],
            ["checks"] = ["build", "test"]
        });

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        var output = Assert.IsType<JsonObject>(json["hookSpecificOutput"]);
        Assert.Equal("PreToolUse", output["hookEventName"]?.GetValue<string>());
        Assert.Equal("deny", output["permissionDecision"]?.GetValue<string>());
        Assert.Null(output["decision"]);
        var reason = output["permissionDecisionReason"]?.GetValue<string>();
        Assert.NotNull(reason);
        Assert.StartsWith("CodeIsland HUD answer: ", reason);
        Assert.Contains("\"approach\":{\"answers\":[\"safe\"]}", reason);
        Assert.Contains("\"checks\":{\"answers\":[\"build\",\"test\"]}", reason);
    }

    [Fact]
    public void BuildQuestionDismissResponse_CodexRequestUserInputPreToolUse_DeniesToolCall()
    {
        var evt = MakeEvent("PreToolUse", "codex", "request_user_input", new
        {
            questions = new[] { new { id = "next", question = "Next?" } }
        });

        var response = HookResponseBuilder.BuildQuestionDismissResponse(evt, "dismissed");

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        var output = Assert.IsType<JsonObject>(json["hookSpecificOutput"]);
        Assert.Equal("PreToolUse", output["hookEventName"]?.GetValue<string>());
        Assert.Equal("deny", output["permissionDecision"]?.GetValue<string>());
        var reason = output["permissionDecisionReason"]?.GetValue<string>();
        Assert.NotNull(reason);
        Assert.Contains("dismissed", reason);
    }

    [Fact]
    public void BuildQuestionAnswerResponse_CodexRequestUserInputPermissionRequest_DoesNotEmitUnsupportedUpdatedInput()
    {
        var evt = MakeEvent("PermissionRequest", "codex", "request_user_input", new
        {
            questions = new[] { new { id = "next", question = "Next?" } }
        });
        var question = new QuestionData
        {
            SessionId = "test-123",
            IsCodexRequestUserInput = true
        };

        var response = HookResponseBuilder.BuildQuestionAnswerResponse(evt, question, new Dictionary<string, IReadOnlyList<string>>
        {
            ["next"] = ["continue"]
        });

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        var output = Assert.IsType<JsonObject>(json["hookSpecificOutput"]);
        var decision = Assert.IsType<JsonObject>(output["decision"]);
        Assert.Equal("allow", decision["behavior"]?.GetValue<string>());
        Assert.Null(decision["updatedInput"]);
    }

    [Fact]
    public void BuildQuestionDismissResponse_CodexRequestUserInputPermissionRequest_DeniesDecision()
    {
        var evt = MakeEvent("PermissionRequest", "codex", "functions.request_user_input", new
        {
            questions = new[] { new { id = "next", question = "Next?" } }
        });

        var response = HookResponseBuilder.BuildQuestionDismissResponse(evt, "dismissed");

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        var decision = Assert.IsType<JsonObject>(json["hookSpecificOutput"]?["decision"]);
        Assert.Equal("deny", decision["behavior"]?.GetValue<string>());
        Assert.Equal("dismissed", decision["reason"]?.GetValue<string>());
    }

    [Fact]
    public void BuildQuestionAnswerResponse_AskUserQuestion_KeepsUpdatedInputAnswersAsStrings()
    {
        var evt = MakeEvent("PermissionRequest", "claude", "AskUserQuestion", new
        {
            questions = new object[]
            {
                new { question = "Which approach?" },
                new { question = "Which checks?", multiSelect = true }
            }
        });
        var question = ExtractQuestion(evt);

        var response = HookResponseBuilder.BuildQuestionAnswerResponse(evt, question, new Dictionary<string, IReadOnlyList<string>>
        {
            ["Which approach?"] = ["safe"],
            ["Which checks?"] = ["build", "test"]
        });

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        var output = Assert.IsType<JsonObject>(json["hookSpecificOutput"]);
        var updatedInput = Assert.IsType<JsonObject>(output["updatedInput"]);
        var answers = Assert.IsType<JsonObject>(updatedInput["answers"]);
        Assert.Equal("safe", answers["Which approach?"]?.GetValue<string>());
        Assert.Equal("build, test", answers["Which checks?"]?.GetValue<string>());
        Assert.Equal("allow", output["decision"]?["behavior"]?.GetValue<string>());
        Assert.Null(output["decision"]?["updatedInput"]);
    }

    [Fact]
    public void BuildPermissionAllowResponse_ClaudePermissionRequest_KeepsDecisionAllowShape()
    {
        var evt = MakeEvent("PermissionRequest", "claude", "Bash", new { command = "git status" });

        var response = HookResponseBuilder.BuildPermissionAllowResponse(evt, always: true);

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        var output = Assert.IsType<JsonObject>(json["hookSpecificOutput"]);
        Assert.Equal("PermissionRequest", output["hookEventName"]?.GetValue<string>());
        Assert.Equal("allow", output["decision"]?["behavior"]?.GetValue<string>());
        Assert.Null(output["decision"]?["updatedInput"]);
    }

    [Fact]
    public void BuildPermissionDenyResponse_CodexPermissionRequest_KeepsDecisionDenyShape()
    {
        var evt = MakeEvent("PermissionRequest", "codex", "Bash", new { command = "npm run dev" });

        var response = HookResponseBuilder.BuildPermissionDenyResponse(evt, "user denied");

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        var output = Assert.IsType<JsonObject>(json["hookSpecificOutput"]);
        Assert.Equal("PermissionRequest", output["hookEventName"]?.GetValue<string>());
        Assert.Equal("deny", output["decision"]?["behavior"]?.GetValue<string>());
        Assert.Equal("user denied", output["decision"]?["reason"]?.GetValue<string>());
    }

    [Fact]
    public void BuildPermissionAllowResponse_CodexPreToolUse_ReturnsEmptyResponseBecauseAllowWithoutUpdatedInputIsUnsupported()
    {
        var evt = MakeEvent("PreToolUse", "codex", "Bash", new { command = "npm run dev", approval_required = true });

        var response = HookResponseBuilder.BuildPermissionAllowResponse(evt, always: false);

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        Assert.Empty(json);
    }

    [Fact]
    public void BuildPermissionDenyResponse_CodexPreToolUse_KeepsPreToolUsePermissionDecisionDenyShape()
    {
        var evt = MakeEvent("PreToolUse", "codex", "Bash", new { command = "npm run dev", approval_required = true });

        var response = HookResponseBuilder.BuildPermissionDenyResponse(evt, "user denied");

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        var output = Assert.IsType<JsonObject>(json["hookSpecificOutput"]);
        Assert.Equal("PreToolUse", output["hookEventName"]?.GetValue<string>());
        Assert.Equal("deny", output["permissionDecision"]?.GetValue<string>());
        Assert.Equal("user denied", output["permissionDecisionReason"]?.GetValue<string>());
    }

    [Fact]
    public void BuildQuestionAnswerResponse_LegacySingleQuestion_KeepsAnswerShape()
    {
        var evt = MakeEvent("QuestionRequest", "claude", null, null, rawExtra: new Dictionary<string, object?>
        {
            ["question"] = "Continue?"
        });
        var question = ExtractQuestion(evt);

        var response = HookResponseBuilder.BuildQuestionAnswerResponse(evt, question, new Dictionary<string, IReadOnlyList<string>>
        {
            ["Continue?"] = ["yes"]
        });

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(response));
        Assert.Equal("yes", json["answer"]?.GetValue<string>());
    }

    private static HookEvent MakeEvent(
        string eventName,
        string source,
        string? toolName,
        object? toolInput,
        Dictionary<string, object?>? rawExtra = null)
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = eventName,
            ["session_id"] = "test-123",
            ["_source"] = source
        };
        if (!string.IsNullOrWhiteSpace(toolName))
            json["tool_name"] = toolName;
        if (toolInput != null)
            json["tool_input"] = toolInput;
        if (rawExtra != null)
        {
            foreach (var pair in rawExtra)
                json[pair.Key] = pair.Value;
        }

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        return HookEvent.FromJson(doc.RootElement, source)!;
    }

    private static QuestionData ExtractQuestion(HookEvent evt)
    {
        var (_, effect) = SessionSnapshot.ReduceEvent(null, evt);
        return Assert.IsType<SideEffect.ShowQuestionCard>(effect).Question;
    }
}
