namespace CodeIsland.Bridge;

/// <summary>
/// Selects the process whose lifetime should be tracked by the App for session cleanup.
/// </summary>
public static class TrackedProcessResolver
{
    private static readonly HashSet<string> ShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pwsh",
        "powershell",
        "cmd",
        "bash",
        "zsh",
        "wsl",
        "nu"
    };

    private static readonly HashSet<string> ToolProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude",
        "codex",
        "gemini",
        "cursor-agent",
        "qoder",
        "qoder-cli",
        "factory",
        "codebuddy",
        "opencode",
        "cline",
        "trae",
        "traecli",
        "copilot",
        "node"
    };

    public static TrackedProcess Resolve(
        IReadOnlyList<ProcessInfo> ancestry,
        int parentPid,
        IReadOnlyDictionary<string, string>? terminalEnvironment = null)
    {
        if (ShouldPreferCliLifecycle(ancestry, terminalEnvironment))
        {
            var cliProcess = FindToolProcess(ancestry);
            if (cliProcess is not null)
                return new TrackedProcess(cliProcess.Pid, "cli", cliProcess.StartedAtUtc);
        }

        var shellProcess = FindShellProcess(ancestry);
        if (shellProcess is not null)
            return new TrackedProcess(shellProcess.Pid, "shell", shellProcess.StartedAtUtc);

        var toolProcess = FindToolProcess(ancestry);
        if (toolProcess is not null)
            return new TrackedProcess(toolProcess.Pid, "cli", toolProcess.StartedAtUtc);

        var parentProcess = ancestry.FirstOrDefault(process => process.Pid == parentPid);
        return new TrackedProcess(parentPid, "parent", parentProcess?.StartedAtUtc);
    }

    private static ProcessInfo? FindShellProcess(IReadOnlyList<ProcessInfo> ancestry)
    {
        foreach (var process in ancestry)
        {
            if (IsShellProcess(process))
                return process;
        }

        return null;
    }

    private static ProcessInfo? FindToolProcess(IReadOnlyList<ProcessInfo> ancestry)
    {
        foreach (var process in ancestry)
        {
            if (IsToolProcess(process))
                return process;
        }

        return null;
    }

    private static bool ShouldPreferCliLifecycle(
        IReadOnlyList<ProcessInfo> ancestry,
        IReadOnlyDictionary<string, string>? terminalEnvironment)
    {
        if (terminalEnvironment is not null &&
            terminalEnvironment.TryGetValue("TERMINAL_EMULATOR", out var terminalEmulator) &&
            !string.IsNullOrWhiteSpace(terminalEmulator) &&
            (terminalEmulator.Contains("JetBrains-JediTerm", StringComparison.OrdinalIgnoreCase) ||
             terminalEmulator.Contains("JediTerm", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ancestry.Any(IsJetBrainsIdeProcess);
    }

    private static bool IsJetBrainsIdeProcess(ProcessInfo process)
    {
        var name = NormalizeProcessName(process.Name);
        if (name.Contains("idea", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("rider", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("webstorm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("pycharm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("clion", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("goland", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("phpstorm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("rubymine", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("datagrip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return process.ExecutablePath.Contains("JetBrains", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShellProcess(ProcessInfo process) =>
        ShellProcessNames.Contains(NormalizeProcessName(process.Name));

    private static bool IsToolProcess(ProcessInfo process)
    {
        var name = NormalizeProcessName(process.Name);
        if (ToolProcessNames.Contains(name))
            return true;

        var path = process.ExecutablePath;
        return path.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("gemini", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("opencode", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("cline", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("cursor-agent", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProcessName(string processName) =>
        Path.GetFileNameWithoutExtension(processName.Trim());
}

public sealed record TrackedProcess(int Pid, string Kind, DateTime? StartedAtUtc = null);
