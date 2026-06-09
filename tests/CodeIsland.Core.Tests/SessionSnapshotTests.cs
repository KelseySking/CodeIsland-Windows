using System.Text.Json;
using CodeIsland.Core.Models;
using Xunit;

namespace CodeIsland.Core.Tests;

public class SessionSnapshotTests
{
    private static HookEvent MakeEvent(string eventName, string? source = "claude",
        string? sessionId = "test-123", string? toolName = null)
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = eventName,
            ["session_id"] = sessionId,
        };
        if (toolName != null) json["tool_name"] = toolName;

        var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        return HookEvent.FromJson(doc.RootElement, source)!;
    }

    [Fact]
    public void UserPromptSubmit_SetsProcessing()
    {
        var evt = MakeEvent("UserPromptSubmit");
        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.Processing, state.Status);
        Assert.IsType<SideEffect.None>(effect);
    }

    [Fact]
    public void PreToolUse_SetsRunning()
    {
        var evt = MakeEvent("PreToolUse", toolName: "Bash");
        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.Running, state.Status);
        Assert.Equal("Bash", state.CurrentToolName);
    }

    [Fact]
    public void PreToolUse_WithExplicitApprovalSignal_ShowsApprovalCard()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "test-123",
            ["tool_name"] = "Bash",
            ["tool_use_id"] = "tool-1",
            ["tool_input"] = new { command = "rm -rf temp", requires_approval = true }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.WaitingApproval, state.Status);
        var approval = Assert.IsType<SideEffect.ShowApprovalCard>(effect);
        Assert.Equal("PreToolUse", approval.Request.HookEventName);
        Assert.Equal("Bash", approval.Request.ToolName);
    }

    [Fact]
    public void PreToolUse_WithoutExplicitApprovalSignal_DoesNotShowApprovalCard()
    {
        var evt = MakeEvent("PreToolUse", toolName: "Bash");

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.Running, state.Status);
        Assert.IsType<SideEffect.None>(effect);
    }

    [Fact]
    public void PermissionRequest_UsesCurrentToolMetadataWhenPayloadIsThin()
    {
        var preJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "test-123",
            ["tool_name"] = "Bash",
            ["tool_use_id"] = "tool-1",
            ["tool_input"] = new { command = "dotnet test" }
        };
        using var preDoc = JsonDocument.Parse(JsonSerializer.Serialize(preJson));
        var pre = HookEvent.FromJson(preDoc.RootElement, "claude")!;
        var (running, _) = SessionSnapshot.ReduceEvent(null, pre);

        var permissionJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["session_id"] = "test-123",
            ["tool_use_id"] = "tool-1"
        };
        using var permissionDoc = JsonDocument.Parse(JsonSerializer.Serialize(permissionJson));
        var permission = HookEvent.FromJson(permissionDoc.RootElement, "claude")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(running, permission);

        Assert.Equal(AgentStatus.WaitingApproval, state.Status);
        var approval = Assert.IsType<SideEffect.ShowApprovalCard>(effect);
        Assert.Equal("Bash", approval.Request.ToolName);
        Assert.Equal("dotnet test", approval.Request.Description);
    }

    [Fact]
    public void PostToolUse_SetsProcessingAndClearsTool()
    {
        var evt = MakeEvent("PreToolUse", toolName: "Bash");
        var (state1, _) = SessionSnapshot.ReduceEvent(null, evt);

        var evt2 = MakeEvent("PostToolUse", toolName: "Bash");
        var (state2, _) = SessionSnapshot.ReduceEvent(state1, evt2);

        Assert.Equal(AgentStatus.Processing, state2.Status);
        Assert.Null(state2.CurrentToolName);
        Assert.Null(state2.CurrentToolDescription);
        Assert.Single(state2.ToolHistory);
    }

    [Fact]
    public void UserPromptSubmit_AfterPostToolUse_SetsProcessingForModelTurn()
    {
        var pre = MakeEvent("PreToolUse", toolName: "Bash");
        var (state1, _) = SessionSnapshot.ReduceEvent(null, pre);
        var post = MakeEvent("PostToolUse", toolName: "Bash");
        var (state2, _) = SessionSnapshot.ReduceEvent(state1, post);

        Assert.Equal(AgentStatus.Processing, state2.Status);

        var prompt = MakeEvent("UserPromptSubmit");
        var (state3, _) = SessionSnapshot.ReduceEvent(state2, prompt);

        Assert.Equal(AgentStatus.Processing, state3.Status);
    }

    [Fact]
    public void ConsecutiveToolCalls_TransitionProcessingThenRunning()
    {
        var pre1 = MakeEvent("PreToolUse", toolName: "Bash");
        var (state1, _) = SessionSnapshot.ReduceEvent(null, pre1);
        var post1 = MakeEvent("PostToolUse", toolName: "Bash");
        var (state2, _) = SessionSnapshot.ReduceEvent(state1, post1);

        Assert.Equal(AgentStatus.Processing, state2.Status);

        var pre2 = MakeEvent("PreToolUse", toolName: "Read");
        var (state3, _) = SessionSnapshot.ReduceEvent(state2, pre2);

        Assert.Equal(AgentStatus.Running, state3.Status);
        Assert.Equal("Read", state3.CurrentToolName);
        Assert.Single(state3.ToolHistory);
    }

    [Fact]
    public void Stop_SetsIdle()
    {
        var evt = MakeEvent("UserPromptSubmit");
        var (state1, _) = SessionSnapshot.ReduceEvent(null, evt);

        var evt2 = MakeEvent("Stop");
        var (state2, effect) = SessionSnapshot.ReduceEvent(state1, evt2);

        Assert.Equal(AgentStatus.Idle, state2.Status);
        Assert.Null(state2.CompletionText);
        Assert.DoesNotContain(state2.RecentMessages, message => message.Text == "[回复完成]");
        Assert.IsType<SideEffect.None>(effect);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("SessionEnd")]
    public void TerminalLifecycleEvent_ClearsCurrentToolDisplay(string eventName)
    {
        var preJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "test-123",
            ["tool_name"] = "Bash",
            ["tool_input"] = new { command = "dotnet test" }
        };
        using var preDoc = JsonDocument.Parse(JsonSerializer.Serialize(preJson));
        var pre = HookEvent.FromJson(preDoc.RootElement, "claude")!;
        var (running, _) = SessionSnapshot.ReduceEvent(null, pre);

        Assert.Equal("Bash", running.CurrentToolName);
        Assert.Equal("dotnet test", running.CurrentToolDescription);

        var terminal = MakeEvent(eventName);
        var (state, effect) = SessionSnapshot.ReduceEvent(running, terminal);

        Assert.Equal(AgentStatus.Idle, state.Status);
        Assert.Null(state.CurrentToolName);
        Assert.Null(state.CurrentToolDescription);
        Assert.IsType<SideEffect.None>(effect);
    }

    [Fact]
    public void LowercaseStop_SetsIdle()
    {
        var evt = MakeEvent("PreToolUse", toolName: "Bash");
        var (state1, _) = SessionSnapshot.ReduceEvent(null, evt);

        var evt2 = MakeEvent("stop");
        var (state2, effect) = SessionSnapshot.ReduceEvent(state1, evt2);

        Assert.Equal(AgentStatus.Idle, state2.Status);
        Assert.Null(state2.CompletionText);
        Assert.IsType<SideEffect.None>(effect);
    }

    [Fact]
    public void SessionStart_CreatesNewSession()
    {
        var evt = MakeEvent("SessionStart");
        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal("test-123", state.SessionId);
        Assert.Equal("claude", state.Source);
        Assert.IsType<SideEffect.PlaySound>(effect);
    }

    [Fact]
    public void ToolHistory_Accumulates()
    {
        var events = new[] { "Bash", "Read", "Edit", "Write" };
        SessionSnapshot? state = null;

        foreach (var tool in events)
        {
            var pre = MakeEvent("PreToolUse", toolName: tool);
            (state, _) = SessionSnapshot.ReduceEvent(state, pre);
            var post = MakeEvent("PostToolUse", toolName: tool);
            (state, _) = SessionSnapshot.ReduceEvent(state, post);
        }

        Assert.Equal(4, state!.ToolHistory.Count);
    }

    [Fact]
    public void QuestionEvent_WithTopLevelQuestion_ShowsQuestionCard()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "QuestionRequest",
            ["session_id"] = "test-123",
            ["question"] = "请选择下一步"
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.WaitingQuestion, state.Status);
        var showQuestion = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.Equal("请选择下一步", showQuestion.Question.Question);
    }

    [Fact]
    public void Notification_WithToolInputQuestions_ShowsQuestionCard()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Notification",
            ["session_id"] = "test-123",
            ["tool_input"] = new
            {
                questions = new[] { "继续执行吗？", "是否运行测试？" }
            }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.WaitingQuestion, state.Status);
        var showQuestion = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.Contains("继续执行吗？", showQuestion.Question.Question);
        Assert.Contains("是否运行测试？", showQuestion.Question.Question);
    }

    [Fact]
    public void SessionStart_ReadsInjectedWtSession()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "test-123",
            ["_wt_session"] = "wt-session-id"
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal("wt-session-id", state.TerminalSessionId);
    }

    [Fact]
    public void SessionStart_ExtractsProjectNameFromCwd()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "test-123",
            ["cwd"] = @"D:\Work\my-project"
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(@"D:\Work\my-project", state.WorkingDirectory);
        Assert.Equal("my-project", state.ProjectName);
    }

    [Fact]
    public void SessionStart_ExtractsProjectNameFromTranscriptPath()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "test-123",
            ["transcript_path"] = @"D:\Work\my-project\.claude\transcript.jsonl"
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(@"D:\Work\my-project", state.WorkingDirectory);
        Assert.Equal("my-project", state.ProjectName);
    }

    [Theory]
    [InlineData("current_dir")]
    [InlineData("currentDir")]
    [InlineData("working_directory")]
    [InlineData("workingDirectory")]
    [InlineData("workspace")]
    [InlineData("workspaceFolder")]
    [InlineData("transcriptPath")]
    public void SessionStart_ExtractsProjectNameFromSupportedDirectoryFields(string fieldName)
    {
        var value = fieldName == "transcriptPath"
            ? @"D:\Work\my-project\.claude\transcript.jsonl"
            : @"D:\Work\my-project";
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "test-123",
            [fieldName] = value
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(@"D:\Work\my-project", state.WorkingDirectory);
        Assert.Equal("my-project", state.ProjectName);
    }

    [Theory]
    [InlineData(@"C:\Users\amiya\.claude\projects\-D-OtherWork-CodeIsland.Windows\transcript.jsonl")]
    [InlineData(@"C:\Users\amiya\.claude\projects\D--OtherWork-CodeIsland.Windows\transcript.jsonl")]
    public void SessionStart_DecodesClaudeProjectsTranscriptPath(string transcriptPath)
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "test-123",
            ["transcript_path"] = transcriptPath
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(@"D:\OtherWork\CodeIsland.Windows", state.WorkingDirectory);
        Assert.Equal("CodeIsland.Windows", state.ProjectName);
    }

    [Fact]
    public void SessionEnd_StaysIdleForAppStateRemoval()
    {
        var started = MakeEvent("UserPromptSubmit");
        var (state1, _) = SessionSnapshot.ReduceEvent(null, started);

        var ended = MakeEvent("SessionEnd");
        var (state2, effect) = SessionSnapshot.ReduceEvent(state1, ended);

        Assert.Equal(AgentStatus.Idle, state2.Status);
        Assert.IsType<SideEffect.None>(effect);
    }

    [Fact]
    public void ReduceEvent_UsesTrackedPidBeforeParentPid()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "test-123",
            ["_ppid"] = 1234,
            ["_tracked_pid"] = 5678,
            ["_tracked_pid_kind"] = "shell",
            ["_tracked_process_started_at_utc"] = "2026-06-08T10:11:12.0000000Z"
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(5678, state.Pid);
        Assert.Equal(new DateTime(2026, 6, 8, 10, 11, 12, DateTimeKind.Utc), state.TrackedProcessStartedAtUtc);
    }

    [Fact]
    public void UserPromptSubmit_CapturesRecentUserMessage()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "修复 Claude Code 监听"
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal("修复 Claude Code 监听", state.LastUserPrompt);
        var message = Assert.Single(state.RecentMessages);
        Assert.True(message.IsUser);
        Assert.Equal("修复 Claude Code 监听", message.Text);
    }

    [Fact]
    public void Stop_CapturesAssistantMessageAndCompletionText()
    {
        var promptJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "开始"
        };
        using var promptDoc = JsonDocument.Parse(JsonSerializer.Serialize(promptJson));
        var prompt = HookEvent.FromJson(promptDoc.RootElement, "claude")!;
        var (state1, _) = SessionSnapshot.ReduceEvent(null, prompt);

        var stopJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = "test-123",
            ["last_assistant_message"] = "已经完成"
        };
        using var stopDoc = JsonDocument.Parse(JsonSerializer.Serialize(stopJson));
        var stop = HookEvent.FromJson(stopDoc.RootElement, "claude")!;

        var (state2, effect) = SessionSnapshot.ReduceEvent(state1, stop);

        Assert.Equal("已经完成", state2.LastAssistantMessage);
        Assert.Equal("已经完成", state2.CompletionText);
        Assert.Equal(2, state2.RecentMessages.Count);
        Assert.False(state2.RecentMessages.Last().IsUser);
        var sound = Assert.IsType<SideEffect.PlaySound>(effect);
        Assert.Equal("complete", sound.SoundName);
    }

    [Fact]
    public void PostToolUseFailure_RecordsFailedToolAndReturnsProcessing()
    {
        var pre = MakeEvent("PreToolUse", toolName: "Bash");
        var (running, _) = SessionSnapshot.ReduceEvent(null, pre);
        var failure = MakeEvent("post_tool_use_failure", toolName: "Bash");

        var (state, effect) = SessionSnapshot.ReduceEvent(running, failure);

        Assert.Equal(AgentStatus.Processing, state.Status);
        var history = Assert.Single(state.ToolHistory);
        Assert.False(history.Success);
        Assert.IsType<SideEffect.None>(effect);
    }

    [Fact]
    public void SubagentStartAndStop_UpdateRunningAndProcessing()
    {
        var startJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "subagent_start",
            ["session_id"] = "test-123",
            ["agent_type"] = "Explore"
        };
        using var startDoc = JsonDocument.Parse(JsonSerializer.Serialize(startJson));
        var start = HookEvent.FromJson(startDoc.RootElement, "claude")!;

        var (running, _) = SessionSnapshot.ReduceEvent(null, start);
        Assert.Equal(AgentStatus.Running, running.Status);
        Assert.Equal("Agent", running.CurrentToolName);
        Assert.Equal("Explore", running.CurrentToolDescription);

        var stop = MakeEvent("subagent_stop");
        var (processing, _) = SessionSnapshot.ReduceEvent(running, stop);
        Assert.Equal(AgentStatus.Processing, processing.Status);
        Assert.Null(processing.CurrentToolName);
    }

    [Fact]
    public void PermissionRequestAskUserQuestion_ShowsQuestionCard()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["session_id"] = "test-123",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = new { question = "选择方案？" }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.WaitingQuestion, state.Status);
        var question = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.Equal("选择方案？", question.Question.Question);
        Assert.Equal("PermissionRequest", question.Question.HookEventName);
        Assert.True(question.Question.IsAskUserQuestion);
    }

    [Fact]
    public void PreToolUseAskUserQuestion_ShowsQuestionCard()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "test-123",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = new { question = "选择方案？" }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.WaitingQuestion, state.Status);
        var question = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.Equal("选择方案？", question.Question.Question);
        Assert.Equal("PreToolUse", question.Question.HookEventName);
        Assert.True(question.Question.IsAskUserQuestion);
        Assert.True(question.Question.OriginalInput?.TryGetProperty("question", out _) == true);
    }

    [Fact]
    public void AskUserQuestionQuestionsArray_ParsesNestedOptionsAndMultiSelect()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["session_id"] = "test-123",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = new
            {
                questions = new[]
                {
                    new
                    {
                        question = "选择方案？",
                        header = "方案选择",
                        multiSelect = true,
                        options = new[]
                        {
                            new { label = "方案 A", description = "更快", value = "a" },
                            new { label = "方案 B", description = "更稳", value = "b" }
                        }
                    }
                }
            }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (_, effect) = SessionSnapshot.ReduceEvent(null, evt);

        var showQuestion = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.Equal("选择方案？", showQuestion.Question.Question);
        Assert.Equal("方案选择", showQuestion.Question.Header);
        Assert.True(showQuestion.Question.MultiSelect);
        var option = Assert.Single(showQuestion.Question.Options!, opt => opt.Label == "方案 A");
        Assert.Equal("更快", option.Description);
        Assert.Equal("a", option.Value);
        var item = Assert.Single(showQuestion.Question.Questions!);
        Assert.True(item.MultiSelect);
        Assert.Equal("方案选择", item.Header);
    }

    [Fact]
    public void AskUserQuestionQuestionsArray_ParsesMultipleQuestionsIndependently()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["session_id"] = "test-123",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = new
            {
                questions = new object[]
                {
                    new { question = "第一题？", header = "一", multiSelect = false, options = new[] { new { label = "是", description = "确认" } } },
                    new { question = "第二题？", header = "二", multiSelect = true, options = new[] { new { label = "A", description = "A 描述" }, new { label = "C", description = "C 描述" } } }
                }
            }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (_, effect) = SessionSnapshot.ReduceEvent(null, evt);

        var showQuestion = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.True(showQuestion.Question.IsMultiQuestion);
        Assert.Equal("第一题？" + Environment.NewLine + "第二题？", showQuestion.Question.Question);
        Assert.Equal(2, showQuestion.Question.Questions!.Count);
        Assert.False(showQuestion.Question.Questions[0].MultiSelect);
        Assert.True(showQuestion.Question.Questions[1].MultiSelect);
        Assert.Equal("C 描述", showQuestion.Question.Questions[1].Options![1].Description);
    }

    [Fact]
    public void QuestionEvent_TopLevelOptionsRemainSupported()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "QuestionRequest",
            ["session_id"] = "test-123",
            ["question"] = "继续吗？",
            ["options"] = new[] { "继续", "停止" }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (_, effect) = SessionSnapshot.ReduceEvent(null, evt);

        var showQuestion = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.Equal(new[] { "继续", "停止" }, showQuestion.Question.Options!.Select(static option => option.Label).ToArray());
    }

    [Fact]
    public void AskUserQuestionQuestionsArray_PreservesTextOnlyQuestions()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["session_id"] = "test-123",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = new
            {
                questions = new object[]
                {
                    "请输入原因",
                    new { question = "选择方案？", options = new[] { new { label = "方案 A" } } }
                }
            }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (_, effect) = SessionSnapshot.ReduceEvent(null, evt);

        var showQuestion = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.True(showQuestion.Question.IsMultiQuestion);
        Assert.Equal(2, showQuestion.Question.Questions!.Count);
        Assert.Equal("请输入原因", showQuestion.Question.Questions[0].Question);
        Assert.True(showQuestion.Question.Questions[0].AllowFreeText);
        Assert.Null(showQuestion.Question.Questions[0].Options);
        Assert.Equal("选择方案？", showQuestion.Question.Questions[1].Question);
    }

    [Fact]
    public void CodexRequestUserInput_ParsesStableIdsOptionsAndMultipleFlag()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "test-123",
            ["tool_name"] = "functions.request_user_input",
            ["tool_input"] = new
            {
                questions = new object[]
                {
                    new
                    {
                        id = "approach",
                        header = "Choose approach",
                        question = "Which approach?",
                        options = new[]
                        {
                            new { label = "Fast", description = "Ship quickly", value = "fast" },
                            new { label = "Safe", description = "More checks", value = "safe" }
                        }
                    },
                    new
                    {
                        id = "checks",
                        header = "Checks",
                        question = "Which checks?",
                        multiple = true,
                        options = new[]
                        {
                            new { label = "Build", value = "build" },
                            new { label = "Tests", value = "tests" }
                        }
                    }
                }
            }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "codex")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.WaitingQuestion, state.Status);
        var showQuestion = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.True(showQuestion.Question.IsCodexRequestUserInput);
        Assert.False(showQuestion.Question.IsAskUserQuestion);
        Assert.True(showQuestion.Question.IsMultiQuestion);
        Assert.Equal("approach", showQuestion.Question.Questions![0].Id);
        Assert.Equal("Ship quickly", showQuestion.Question.Questions[0].Options![0].Description);
        Assert.Equal("fast", showQuestion.Question.Questions[0].Options![0].Value);
        Assert.Equal("checks", showQuestion.Question.Questions[1].Id);
        Assert.True(showQuestion.Question.Questions[1].MultiSelect);
    }

    [Fact]
    public void CodexRequestUserInputPreToolUse_ShowsQuestionCard()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "test-123",
            ["tool_name"] = "request_user_input",
            ["tool_input"] = new
            {
                questions = new[]
                {
                    new { id = "next", question = "Next step?" }
                }
            }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "codex")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.WaitingQuestion, state.Status);
        var showQuestion = Assert.IsType<SideEffect.ShowQuestionCard>(effect);
        Assert.True(showQuestion.Question.IsCodexRequestUserInput);
        Assert.Equal("PreToolUse", showQuestion.Question.HookEventName);
        Assert.Equal("next", Assert.Single(showQuestion.Question.Questions!).Id);
    }

    [Fact]
    public void CodexRequestUserInputPermissionRequest_WithNestedFunctionName_ShowsApprovalCard()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["session_id"] = "test-123",
            ["function"] = new { name = "functions.request_user_input" },
            ["tool_input"] = new
            {
                questions = new[]
                {
                    new { id = "next", question = "Next step?" }
                }
            }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "codex")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.WaitingApproval, state.Status);
        var approval = Assert.IsType<SideEffect.ShowApprovalCard>(effect);
        Assert.Equal("functions.request_user_input", approval.Request.ToolName);
        Assert.Equal("PermissionRequest", approval.Request.HookEventName);
    }

    [Fact]
    public void ReduceEvent_FallsBackToParentPidWhenTrackedPidMissing()
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "test-123",
            ["_ppid"] = 1234
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, _) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(1234, state.Pid);
    }

    [Fact]
    public void UserPromptSubmit_WithSystemPlaceholder_DoesNotUpdateLastUserPrompt()
    {
        var firstJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "真实用户提问"
        };
        using var firstDoc = JsonDocument.Parse(JsonSerializer.Serialize(firstJson));
        var firstEvt = HookEvent.FromJson(firstDoc.RootElement, "claude")!;
        var (state1, _) = SessionSnapshot.ReduceEvent(null, firstEvt);

        Assert.Equal("真实用户提问", state1.LastUserPrompt);

        var placeholderJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "<local-command-stdout>\x1b[2mCompacted (ctrl+o to see full summary)\x1b[22m"
        };
        using var placeholderDoc = JsonDocument.Parse(JsonSerializer.Serialize(placeholderJson));
        var placeholderEvt = HookEvent.FromJson(placeholderDoc.RootElement, "claude")!;

        var (state2, _) = SessionSnapshot.ReduceEvent(state1, placeholderEvt);

        Assert.Equal("真实用户提问", state2.LastUserPrompt);
    }

    [Fact]
    public void UserPromptSubmit_WithSystemPlaceholder_DoesNotAppendRecentMessage()
    {
        var firstJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "真实用户提问"
        };
        using var firstDoc = JsonDocument.Parse(JsonSerializer.Serialize(firstJson));
        var firstEvt = HookEvent.FromJson(firstDoc.RootElement, "claude")!;
        var (state1, _) = SessionSnapshot.ReduceEvent(null, firstEvt);

        Assert.Single(state1.RecentMessages);

        var placeholderJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "<local-command-stdout>\x1b[2mCompacted (ctrl+o to see full summary)\x1b[22m"
        };
        using var placeholderDoc = JsonDocument.Parse(JsonSerializer.Serialize(placeholderJson));
        var placeholderEvt = HookEvent.FromJson(placeholderDoc.RootElement, "claude")!;

        var (state2, _) = SessionSnapshot.ReduceEvent(state1, placeholderEvt);

        Assert.Single(state2.RecentMessages);
        Assert.Equal("真实用户提问", state2.RecentMessages[0].Text);
    }

    [Fact]
    public void AddRecentMessage_WithSystemPlaceholderUserMessage_DoesNotUpdateLastUserPrompt()
    {
        var snapshot = new SessionSnapshot
        {
            SessionId = "test-123",
            LastUserPrompt = "真实用户提问",
            RecentMessages = new List<ChatMessage>
            {
                new() { IsUser = true, Text = "真实用户提问" }
            }
        };

        SessionSnapshot.AddRecentMessage(snapshot, new ChatMessage
        {
            IsUser = true,
            Text = "<local-command-stdout>Compacted"
        });

        Assert.Equal("真实用户提问", snapshot.LastUserPrompt);
        Assert.Single(snapshot.RecentMessages);
    }

    [Theory]
    [InlineData("<local-command-stdout>some output")]
    [InlineData("<local-command-stderr>some error")]
    [InlineData("<command-name>compact")]
    [InlineData("<command-message>doing compact")]
    [InlineData("<command-args>--foo")]
    public void UserPromptSubmit_WithSystemPlaceholder_StillSwitchesToProcessing(string placeholderPrompt)
    {
        var json = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = placeholderPrompt
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var evt = HookEvent.FromJson(doc.RootElement, "claude")!;

        var (state, effect) = SessionSnapshot.ReduceEvent(null, evt);

        Assert.Equal(AgentStatus.Processing, state.Status);
        Assert.IsType<SideEffect.None>(effect);
        Assert.Null(state.LastUserPrompt);
        Assert.Empty(state.RecentMessages);
    }

    [Fact]
    public void UserPromptSubmit_WithRealPrompt_ResetsCompletionText()
    {
        var promptJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "第一轮提问"
        };
        using var promptDoc = JsonDocument.Parse(JsonSerializer.Serialize(promptJson));
        var promptEvt = HookEvent.FromJson(promptDoc.RootElement, "claude")!;
        var (state1, _) = SessionSnapshot.ReduceEvent(null, promptEvt);

        var stopJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = "test-123",
            ["last_assistant_message"] = "第一轮回答"
        };
        using var stopDoc = JsonDocument.Parse(JsonSerializer.Serialize(stopJson));
        var stopEvt = HookEvent.FromJson(stopDoc.RootElement, "claude")!;
        var (state2, _) = SessionSnapshot.ReduceEvent(state1, stopEvt);

        Assert.Equal("第一轮回答", state2.CompletionText);

        var newPromptJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "第二轮提问"
        };
        using var newPromptDoc = JsonDocument.Parse(JsonSerializer.Serialize(newPromptJson));
        var newPromptEvt = HookEvent.FromJson(newPromptDoc.RootElement, "claude")!;

        var (state3, _) = SessionSnapshot.ReduceEvent(state2, newPromptEvt);

        Assert.Null(state3.CompletionText);
        Assert.Equal("第二轮提问", state3.LastUserPrompt);
    }

    [Fact]
    public void UserPromptSubmit_WithRealPrompt_ResetsLastAssistantMessage()
    {
        var promptJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "第一轮提问"
        };
        using var promptDoc = JsonDocument.Parse(JsonSerializer.Serialize(promptJson));
        var promptEvt = HookEvent.FromJson(promptDoc.RootElement, "claude")!;
        var (state1, _) = SessionSnapshot.ReduceEvent(null, promptEvt);

        var stopJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = "test-123",
            ["last_assistant_message"] = "第一轮回答"
        };
        using var stopDoc = JsonDocument.Parse(JsonSerializer.Serialize(stopJson));
        var stopEvt = HookEvent.FromJson(stopDoc.RootElement, "claude")!;
        var (state2, _) = SessionSnapshot.ReduceEvent(state1, stopEvt);

        Assert.Equal("第一轮回答", state2.LastAssistantMessage);

        var newPromptJson = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = "test-123",
            ["prompt"] = "第二轮提问"
        };
        using var newPromptDoc = JsonDocument.Parse(JsonSerializer.Serialize(newPromptJson));
        var newPromptEvt = HookEvent.FromJson(newPromptDoc.RootElement, "claude")!;

        var (state3, _) = SessionSnapshot.ReduceEvent(state2, newPromptEvt);

        Assert.Null(state3.LastAssistantMessage);
    }
}
