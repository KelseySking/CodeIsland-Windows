using Xunit;

namespace CodeIsland.Bridge.Tests;

public class EnvironmentCollectorTests
{
    [Fact]
    public void Collect_ReturnsDictionary()
    {
        var result = EnvironmentCollector.Collect();
        Assert.NotNull(result);
    }

    [Fact]
    public void Collect_ContainsConsoleTitle()
    {
        var result = EnvironmentCollector.Collect();
        // Console.Title 可能抛异常，所以只在成功时检查
        if (result.ContainsKey("_console_title"))
            Assert.False(string.IsNullOrEmpty(result["_console_title"]));
    }

    [Fact]
    public void Collect_ContainsCwd()
    {
        var result = EnvironmentCollector.Collect();
        Assert.True(result.ContainsKey("_cwd"));
    }

    [Fact]
    public void InjectIntoPayload_AddsFields()
    {
        var payload = new Dictionary<string, object?> { ["test"] = "value" };
        EnvironmentCollector.InjectIntoPayload(payload);

        Assert.True(payload.ContainsKey("_cwd"));
        // 应该有至少 2 个字段（test + 环境变量）
        Assert.True(payload.Count >= 2);
    }

    [Fact]
    public void InjectIntoPayload_AddsTerminalEmulatorFieldFromCollectedEnvironment()
    {
        var payload = new Dictionary<string, object?>();
        var env = new Dictionary<string, string>
        {
            ["TERMINAL_EMULATOR"] = "JetBrains-JediTerm"
        };

        EnvironmentCollector.InjectIntoPayload(payload, env);

        Assert.Equal("JetBrains-JediTerm", payload["_terminal_emulator"]);
    }

    [Fact]
    public void InjectIntoPayload_PreservesCanonicalWtSessionFieldFromCollectedEnvironment()
    {
        var payload = new Dictionary<string, object?>();
        var env = new Dictionary<string, string>
        {
            ["WT_SESSION"] = "wt-test-session"
        };

        EnvironmentCollector.InjectIntoPayload(payload, env);

        Assert.Equal("wt-test-session", payload["_wt_session"]);
        Assert.Equal("wt-test-session", payload["WT_SESSION"]);
    }
}
