using CodeIsland.Core.Sources;

namespace CodeIsland.Core.Models;

/// <summary>
/// 支持的 AI 编程工具来源定义
/// </summary>
public static class SupportedSource
{
    public static IReadOnlyCollection<string> All => CodeIslandSourceAdapterRegistry.KnownSources;

    public static bool IsValid(string source) =>
        CodeIslandSourceAdapterRegistry.IsKnownSource(source);

    public static string GetDisplayName(string source)
    {
        if (CodeIslandSourceAdapterRegistry.TryGet(source, out var adapter))
            return adapter.DisplayName;

        return source switch
        {
            "unknown" => "未知工具",
            "codeisland" => "CodeIsland",
            _ => source
        };
    }

    public static string GetIconName(string source)
    {
        if (CodeIslandSourceAdapterRegistry.TryGet(source, out var adapter))
            return adapter.IconName;

        return source;
    }
}
