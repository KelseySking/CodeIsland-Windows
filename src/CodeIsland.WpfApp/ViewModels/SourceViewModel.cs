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
    private bool _isWslOperating;
    private bool _wslInstalled;
    private bool _wslAvailable;
    private string? _selectedDistro;

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

    public bool WslAvailable
    {
        get => _wslAvailable;
        set
        {
            if (_wslAvailable == value) return;
            _wslAvailable = value;
            OnPropertyChanged();
        }
    }

    public bool WslInstalled
    {
        get => _wslInstalled;
        set
        {
            if (_wslInstalled == value) return;
            _wslInstalled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WslButtonContent));
        }
    }

    public string? SelectedDistro
    {
        get => _selectedDistro;
        set
        {
            if (_selectedDistro == value) return;
            _selectedDistro = value;
            OnPropertyChanged();
        }
    }

    public string WslButtonContent => WslInstalled ? "WSL 断开" : "WSL 连接";

    public bool IsWslOperating
    {
        get => _isWslOperating;
        set
        {
            if (_isWslOperating == value) return;
            _isWslOperating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WslButtonEnabled));
        }
    }

    public bool WslButtonEnabled => !IsWslOperating && WslAvailable;

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
