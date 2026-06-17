using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodeIsland.Contracts;
using CodeIsland.WpfApp.Models;

namespace CodeIsland.WpfApp.ViewModels;

public sealed class SourceViewModel : INotifyPropertyChanged
{
    private readonly SourceDto _dto;
    private readonly string? _sourceIconUri;
    private bool _isOperating;

    public SourceViewModel(SourceDto dto)
    {
        _dto = dto;
        _sourceIconUri = WpfSourceDisplay.GetCliIconUri(dto.IconName, dto.Id, dto.DisplayName);
    }

    public string Id => _dto.Id;
    public string DisplayName => _dto.DisplayName;
    public bool Installed => _dto.Installed;

    public string IconFallbackText => GetFallbackText(DisplayName);
    public string? SourceIconUri => _sourceIconUri;
    public bool HasSourceIcon => !string.IsNullOrWhiteSpace(_sourceIconUri);

    public string ButtonContent => Installed ? "断开" : "连接";

    public bool IsOperating
    {
        get => _isOperating;
        set
        {
            if (_isOperating == value) return;
            _isOperating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ButtonEnabled));
        }
    }

    public bool ButtonEnabled => !IsOperating;

    private static string GetFallbackText(string displayName)
    {
        var trimmed = displayName.Trim();
        return trimmed.Length == 0 ? "?" : trimmed[..1].ToUpperInvariant();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
