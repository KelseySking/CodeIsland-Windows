using CodeIsland.WpfApp.Models;

namespace CodeIsland.WpfApp.ViewModels;

public sealed class WpfHudListGroupViewModel
{
    public WpfHudListGroupViewModel(string sourceKey, IReadOnlyList<WpfHudListItemViewModel> items)
    {
        SourceKey = string.IsNullOrWhiteSpace(sourceKey) ? "unknown" : sourceKey;
        SourceDisplayName = WpfSourceDisplay.GetDisplayName(SourceKey);
        SourceIconUri = WpfSourceDisplay.GetCliIconUri(SourceKey);
        CountText = $"{items.Count} 项";
        IconFallbackText = GetFallbackText(SourceDisplayName);
        Items = items;
    }

    public string SourceKey { get; }
    public string SourceDisplayName { get; }
    public string? SourceIconUri { get; }
    public string CountText { get; }
    public string IconFallbackText { get; }
    public IReadOnlyList<WpfHudListItemViewModel> Items { get; }

    private static string GetFallbackText(string sourceDisplayName)
    {
        var trimmed = sourceDisplayName.Trim();
        return trimmed.Length == 0 ? "?" : trimmed[..1].ToUpperInvariant();
    }
}
