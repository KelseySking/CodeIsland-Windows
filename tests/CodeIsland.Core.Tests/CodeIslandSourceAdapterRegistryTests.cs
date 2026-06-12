using CodeIsland.Core.Sources;
using Xunit;

namespace CodeIsland.Core.Tests;

public class CodeIslandSourceAdapterRegistryTests
{
    [Fact]
    public void Get_Codex_ReturnsCodexPermissionStyle()
    {
        var adapter = CodeIslandSourceAdapterRegistry.Get("codex");

        Assert.Equal("codex", adapter.SourceKey);
        Assert.Equal(CodeIslandPermissionResponseStyle.Codex, adapter.PermissionResponseStyle);
        Assert.Equal("Codex", adapter.DisplayName);
    }

    [Fact]
    public void Get_Unknown_ReturnsFallbackAdapter()
    {
        var adapter = CodeIslandSourceAdapterRegistry.Get("not-a-real-source");

        Assert.Equal("unknown", adapter.SourceKey);
        Assert.Equal("未知工具", adapter.DisplayName);
        Assert.False(CodeIslandSourceAdapterRegistry.IsKnownSource("not-a-real-source"));
    }

    [Fact]
    public void CursorAdapter_NormalizesCursorSpecificEvents()
    {
        var adapter = CodeIslandSourceAdapterRegistry.Get("cursor");

        Assert.True(adapter.TryNormalizeEventName("beforeMcpToolExecution", out var normalized));
        Assert.Equal("PreToolUse", normalized);
    }
}
