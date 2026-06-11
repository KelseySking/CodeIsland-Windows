using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CodeIsland.Core.Models;

namespace CodeIsland.WpfApp.ViewModels;

public enum WpfHudListItemKind
{
    Permission,
    Question,
    Running,
    Completed
}

public sealed class WpfHudListItemViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private string _detailUserPrompt;
    private string _detailAssistantReply;

    public WpfHudListItemViewModel(
        string itemId,
        WpfHudListItemKind kind,
        string? sessionId,
        string title,
        string projectName,
        string summary,
        string sourceKey,
        string sourceDisplayName,
        string statusText,
        AgentStatus status,
        string accentBrush,
        string timeText,
        ICommand openDetailCommand,
        string detailUserPrompt = "",
        string detailAssistantReply = "",
        bool isExpanded = false)
    {
        ItemId = itemId;
        Kind = kind;
        SessionId = sessionId;
        Title = title;
        ProjectName = projectName;
        Summary = summary;
        SourceKey = sourceKey;
        SourceDisplayName = sourceDisplayName;
        StatusText = statusText;
        Status = status;
        AccentBrush = accentBrush;
        TimeText = timeText;
        OpenDetailCommand = openDetailCommand;
        _detailUserPrompt = detailUserPrompt;
        _detailAssistantReply = detailAssistantReply;
        _isExpanded = isExpanded && CanShowInlineSessionDetail;
    }

    public string ItemId { get; }
    public WpfHudListItemKind Kind { get; }
    public string? SessionId { get; }
    public string Title { get; }
    public string ProjectName { get; }
    public string Summary { get; }
    public string SourceKey { get; }
    public string SourceDisplayName { get; }
    public string StatusText { get; }
    public AgentStatus Status { get; }
    public string AccentBrush { get; }
    public string TimeText { get; }
    public ICommand OpenDetailCommand { get; }
    public string DetailUserPrompt
    {
        get => _detailUserPrompt;
        private set
        {
            if (_detailUserPrompt == value)
                return;

            _detailUserPrompt = value;
            OnPropertyChanged();
        }
    }
    public string DetailAssistantReply
    {
        get => _detailAssistantReply;
        private set
        {
            if (_detailAssistantReply == value)
                return;

            _detailAssistantReply = value;
            OnPropertyChanged();
        }
    }
    public bool HasSession => !string.IsNullOrWhiteSpace(SessionId);
    public bool CanJumpToTerminal => Kind is WpfHudListItemKind.Running or WpfHudListItemKind.Completed && HasSession;
    public bool CanRemoveFromHudList => Kind is WpfHudListItemKind.Running or WpfHudListItemKind.Completed && HasSession;
    public bool HasSideActions => CanJumpToTerminal || CanRemoveFromHudList;
    public bool CanShowInlineSessionDetail => Kind is WpfHudListItemKind.Running or WpfHudListItemKind.Completed && HasSession;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            var next = value && CanShowInlineSessionDetail;
            if (_isExpanded == next)
                return;

            _isExpanded = next;
            OnPropertyChanged();
        }
    }

    public void UpdateInlineDetail(string userPrompt, string assistantReply)
    {
        DetailUserPrompt = userPrompt;
        DetailAssistantReply = assistantReply;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
