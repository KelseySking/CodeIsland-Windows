using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodeIsland.WpfApp.Models;

namespace CodeIsland.WpfApp.ViewModels;

public sealed class WpfHudListGroupViewModel : INotifyPropertyChanged
{
    private string _countText;

    public WpfHudListGroupViewModel(string sourceKey)
    {
        SourceKey = string.IsNullOrWhiteSpace(sourceKey) ? "unknown" : sourceKey;
        SourceDisplayName = WpfSourceDisplay.GetDisplayName(SourceKey);
        SourceIconUri = WpfSourceDisplay.GetCliIconUri(SourceKey);
        IconFallbackText = GetFallbackText(SourceDisplayName);
        _countText = "0 项";
        Items = new ObservableCollection<WpfHudListItemViewModel>();
    }

    public string SourceKey { get; }
    public string SourceDisplayName { get; }
    public string? SourceIconUri { get; }
    public string IconFallbackText { get; }
    public ObservableCollection<WpfHudListItemViewModel> Items { get; }

    public string CountText
    {
        get => _countText;
        private set
        {
            if (_countText == value)
                return;
            _countText = value;
            OnPropertyChanged();
        }
    }

    public void SyncItems(IReadOnlyList<WpfHudListItemViewModel> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < Items.Count)
            {
                if (!ReferenceEquals(Items[i], items[i]))
                    Items[i] = items[i];
            }
            else
            {
                Items.Add(items[i]);
            }
        }

        while (Items.Count > items.Count)
            Items.RemoveAt(Items.Count - 1);

        CountText = $"{items.Count} 项";
    }

    private static string GetFallbackText(string sourceDisplayName)
    {
        var trimmed = sourceDisplayName.Trim();
        return trimmed.Length == 0 ? "?" : trimmed[..1].ToUpperInvariant();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
