using Xunit;

namespace CodeIsland.Bridge.Tests;

public class TrackedProcessResolverTests
{
    [Fact]
    public void BuildAncestry_DefaultDepthReachesWrapperHeavyProcessTrees()
    {
        var method = typeof(ProcessAncestry).GetMethod(nameof(ProcessAncestry.BuildAncestry));
        var maxDepth = method!.GetParameters().Single(parameter => parameter.Name == "maxDepth");

        Assert.Equal(12, maxDepth.DefaultValue);
    }

    [Fact]
    public void Resolve_PrefersDeepShellProcessOverNearbyCliWrappers()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "node.exe", @"C:\Users\test\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js"),
            new(101, 100, "npm.cmd", @"C:\Users\test\AppData\Roaming\npm\npm.cmd"),
            new(102, 101, "cmd-shim.exe", @"C:\Tools\cmd-shim.exe"),
            new(103, 102, "node.exe", @"C:\Program Files\nodejs\node.exe"),
            new(104, 103, "not-a-shell.exe", @"C:\Tools\not-a-shell.exe"),
            new(105, 104, "wrapper.exe", @"C:\Tools\wrapper.exe"),
            new(106, 105, "launcher.exe", @"C:\Tools\launcher.exe"),
            new(200, 106, "pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe"),
        };

        var result = TrackedProcessResolver.Resolve(ancestry, parentPid: 100);

        Assert.Equal(200, result.Pid);
        Assert.Equal("shell", result.Kind);
    }

    [Fact]
    public void Resolve_PrefersShellProcess()
    {
        var startedAtUtc = new DateTime(2026, 6, 8, 10, 11, 12, DateTimeKind.Utc);
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "claude.exe", @"C:\Users\test\.claude\claude.exe"),
            new(200, 50, "pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe", startedAtUtc),
        };

        var result = TrackedProcessResolver.Resolve(ancestry, parentPid: 100);

        Assert.Equal(200, result.Pid);
        Assert.Equal("shell", result.Kind);
        Assert.Equal(startedAtUtc, result.StartedAtUtc);
    }

    [Fact]
    public void Resolve_DefaultTerminalEnvironmentStillPrefersShellProcess()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "claude.exe", @"C:\Users\test\.claude\claude.exe"),
            new(200, 50, "pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe"),
        };
        var terminalEnvironment = new Dictionary<string, string>
        {
            ["TERMINAL_EMULATOR"] = "SomeOtherTerminal"
        };

        var result = TrackedProcessResolver.Resolve(ancestry, parentPid: 100, terminalEnvironment);

        Assert.Equal(200, result.Pid);
        Assert.Equal("shell", result.Kind);
    }

    [Fact]
    public void Resolve_JetBrainsJediTermPrefersCliProcessOverShell()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "node.exe", @"C:\Users\test\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js"),
            new(200, 50, "pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe"),
            new(300, 20, "idea64.exe", @"C:\Program Files\JetBrains\IntelliJ IDEA\bin\idea64.exe"),
        };
        var terminalEnvironment = new Dictionary<string, string>
        {
            ["TERMINAL_EMULATOR"] = "JetBrains-JediTerm"
        };

        var result = TrackedProcessResolver.Resolve(ancestry, parentPid: 100, terminalEnvironment);

        Assert.Equal(100, result.Pid);
        Assert.Equal("cli", result.Kind);
    }

    [Fact]
    public void Resolve_JetBrainsAncestorPrefersCliProcessOverShellWithoutEnvironment()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "node.exe", @"C:\Users\test\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js"),
            new(200, 50, "pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe"),
            new(300, 20, "idea64.exe", @"C:\Program Files\JetBrains\IntelliJ IDEA\bin\idea64.exe"),
        };

        var result = TrackedProcessResolver.Resolve(ancestry, parentPid: 100);

        Assert.Equal(100, result.Pid);
        Assert.Equal("cli", result.Kind);
    }

    [Fact]
    public void Resolve_JetBrainsJediTermFallsBackToShellWhenCliMissing()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(200, 50, "pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe"),
            new(300, 20, "idea64.exe", @"C:\Program Files\JetBrains\IntelliJ IDEA\bin\idea64.exe"),
        };
        var terminalEnvironment = new Dictionary<string, string>
        {
            ["TERMINAL_EMULATOR"] = "JetBrains-JediTerm"
        };

        var result = TrackedProcessResolver.Resolve(ancestry, parentPid: 200, terminalEnvironment);

        Assert.Equal(200, result.Pid);
        Assert.Equal("shell", result.Kind);
    }

    [Fact]
    public void Resolve_FallsBackToCliProcessWhenShellMissing()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(100, 99, "node.exe", @"C:\Users\test\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js"),
            new(300, 10, "WindowsTerminal.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe"),
        };

        var result = TrackedProcessResolver.Resolve(ancestry, parentPid: 100);

        Assert.Equal(100, result.Pid);
        Assert.Equal("cli", result.Kind);
    }

    [Fact]
    public void Resolve_FallsBackToParentWhenNoShellOrCliExists()
    {
        var ancestry = new List<ProcessInfo>
        {
            new(500, 400, "notepad.exe", @"C:\Windows\System32\notepad.exe"),
            new(600, 300, "WindowsTerminal.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe"),
        };

        var result = TrackedProcessResolver.Resolve(ancestry, parentPid: 500);

        Assert.Equal(500, result.Pid);
        Assert.Equal("parent", result.Kind);
    }
}
