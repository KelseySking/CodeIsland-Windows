using CodeIsland.Core.Services;
using Xunit;

namespace CodeIsland.Core.Tests;

public class TranscriptMessageReaderTests
{
    [Fact]
    public void ReadNewMessages_ExtractsClaudeUserAndAssistantMessages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeisland-transcript-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, """
                {"type":"user","message":{"role":"user","content":[{"type":"text","text":"你好"}]}}
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"已完成"}]}}
                """.Replace("\r\n", "\n"));

            var result = TranscriptMessageReader.ReadNewMessages(path, 0);

            Assert.Equal(2, result.Messages.Count);
            Assert.True(result.Messages[0].IsUser);
            Assert.Equal("你好", result.Messages[0].Text);
            Assert.False(result.Messages[1].IsUser);
            Assert.Equal("已完成", result.Messages[1].Text);
            Assert.True(result.Position > 0);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReadNewMessages_IgnoresClaudeToolResultBlocks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeisland-transcript-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, """
                {"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"命令输出不应显示"}]}}
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"可见回复"}]}}
                """.Replace("\r\n", "\n"));

            var result = TranscriptMessageReader.ReadNewMessages(path, 0);

            var message = Assert.Single(result.Messages);
            Assert.False(message.IsUser);
            Assert.Equal("可见回复", message.Text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReadNewMessages_ExtractsCodexResponseItemMessages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeisland-transcript-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, """
                {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","input_text":"Please fix the HUD"}]}}
                {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"HUD is fixed"}]}}
                """.Replace("\r\n", "\n"));

            var result = TranscriptMessageReader.ReadNewMessages(path, 0);

            Assert.Equal(2, result.Messages.Count);
            Assert.True(result.Messages[0].IsUser);
            Assert.Equal("Please fix the HUD", result.Messages[0].Text);
            Assert.False(result.Messages[1].IsUser);
            Assert.Equal("HUD is fixed", result.Messages[1].Text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReadNewMessages_IgnoresCodexNonVisibleResponseItems()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeisland-transcript-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, """
                {"type":"response_item","payload":{"type":"function_call","name":"shell","arguments":"{}"}}
                {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"tool_output","output_text":"hidden tool output"}]}}
                {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"visible answer"}]}}
                """.Replace("\r\n", "\n"));

            var result = TranscriptMessageReader.ReadNewMessages(path, 0);

            var message = Assert.Single(result.Messages);
            Assert.False(message.IsUser);
            Assert.Equal("visible answer", message.Text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReadNewMessages_OnlyReadsAfterPreviousPosition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeisland-transcript-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"第一条\"}}\n");
            var first = TranscriptMessageReader.ReadNewMessages(path, 0);
            File.AppendAllText(path, "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":\"第二条\"}}\n");

            var second = TranscriptMessageReader.ReadNewMessages(path, first.Position);

            var message = Assert.Single(second.Messages);
            Assert.False(message.IsUser);
            Assert.Equal("第二条", message.Text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
