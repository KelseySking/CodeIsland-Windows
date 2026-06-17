using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodeIsland.Contracts;

namespace CodeIsland.WpfApp.ViewModels;

public sealed class SourceViewModel : INotifyPropertyChanged
{
    private readonly SourceDto _dto;
    private bool _isOperating;

    public SourceViewModel(SourceDto dto)
    {
        _dto = dto;
    }

    public string Id => _dto.Id;
    public string DisplayName => _dto.DisplayName;
    public bool Installed => _dto.Installed;

    public string IconText => MapIconText(_dto.IconName);

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

    private static string MapIconText(string iconName)
    {
        return iconName?.ToLowerInvariant() switch
        {
            "claude" => "🤖",
            "codex" => "⚙️",
            _ => "🔌"
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
