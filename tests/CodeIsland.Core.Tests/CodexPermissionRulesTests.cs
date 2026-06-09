using CodeIsland.Core.Models;
using CodeIsland.Core.Services;
using Xunit;

namespace CodeIsland.Core.Tests;

public sealed class CodexPermissionRulesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalUserProfileOverride;
    private readonly string? _originalCodexHome;

    public CodexPermissionRulesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codeisland-rules-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalUserProfileOverride = Environment.GetEnvironmentVariable("CODEISLAND_TEST_USERPROFILE");
        _originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEISLAND_TEST_USERPROFILE", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEISLAND_TEST_USERPROFILE", _originalUserProfileOverride);
        Environment.SetEnvironmentVariable("CODEX_HOME", _originalCodexHome);
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }

    [Fact]
    public void TryAppendAllowRule_UsesCodexHomeAndCreatesRulesFile()
    {
        var codexHome = Path.Combine(_tempDir, "custom-codex");
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        var request = new PermissionRequest
        {
            ToolName = "Bash",
            ToolInput = new Dictionary<string, object?>
            {
                ["prefix_rule"] = new[] { "dotnet", "test" },
                ["justification"] = "Run tests"
            }
        };

        Assert.True(CodexPermissionRules.TryAppendAllowRule(request));

        var rulesPath = Path.Combine(codexHome, "rules", "codeisland.rules");
        Assert.True(File.Exists(rulesPath));
        var content = File.ReadAllText(rulesPath);
        Assert.Contains("prefix_rule(pattern = [\"dotnet\", \"test\"], decision = \"allow\", justification = \"Run tests\")", content);
    }

    [Fact]
    public void TryAppendAllowRule_ExpandsTildeCodexHome()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", "~/custom-codex");
        var request = new PermissionRequest
        {
            ToolName = "Bash",
            ToolInput = new Dictionary<string, object?>
            {
                ["prefix_rule"] = new[] { "git", "pull" }
            }
        };

        Assert.True(CodexPermissionRules.TryAppendAllowRule(request));

        Assert.True(File.Exists(Path.Combine(_tempDir, "custom-codex", "rules", "codeisland.rules")));
    }

    [Fact]
    public void TryAppendAllowRule_IsIdempotentAndEscapesStrings()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(_tempDir, ".codex"));
        var request = new PermissionRequest
        {
            ToolName = "Bash",
            ToolInput = new Dictionary<string, object?>
            {
                ["prefix_rule"] = new[] { "git", "checkout \"main\\dev\"" },
                ["justification"] = "Allow \"branch\"\\path"
            }
        };

        Assert.True(CodexPermissionRules.TryAppendAllowRule(request));
        Assert.True(CodexPermissionRules.TryAppendAllowRule(request));

        var content = File.ReadAllText(CodexPermissionRules.RulesFilePath);
        Assert.Single(content.Split("prefix_rule(", StringSplitOptions.None).Skip(1));
        Assert.Contains("\"checkout \\\"main\\\\dev\\\"\"", content);
        Assert.Contains("justification = \"Allow \\\"branch\\\"\\\\path\"", content);
    }

    [Fact]
    public void TryAppendAllowRule_FallsBackToSimpleCommandPrefix()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(_tempDir, ".codex"));
        var request = new PermissionRequest
        {
            ToolName = "Bash",
            ToolInput = new Dictionary<string, object?>
            {
                ["command"] = "npm run dev -- --host"
            }
        };

        Assert.True(CodexPermissionRules.TryAppendAllowRule(request));

        var content = File.ReadAllText(CodexPermissionRules.RulesFilePath);
        Assert.Contains("pattern = [\"npm\", \"run\", \"dev\"]", content);
    }
}
