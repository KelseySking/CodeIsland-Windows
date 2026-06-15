using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CodeIsland.WpfApp.Models;

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
    public string Title { get; private set; }
    public string ProjectName { get; private set; }
    public string Summary { get; private set; }
    public string SourceKey { get; private set; }
    public string SourceDisplayName { get; private set; }
    public string StatusText { get; private set; }
    public AgentStatus Status { get; private set; }
    public string AccentBrush { get; private set; }
    public string TimeText { get; private set; }
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

    public void UpdateSessionPresentation(
        string title,
        string projectName,
        string summary,
        string sourceKey,
        string sourceDisplayName,
        string statusText,
        AgentStatus status,
        string accentBrush,
        string timeText,
        string detailUserPrompt,
        string detailAssistantReply)
    {
        SetProperty(title, value => Title = value, Title, nameof(Title));
        SetProperty(projectName, value => ProjectName = value, ProjectName, nameof(ProjectName));
        SetProperty(summary, value => Summary = value, Summary, nameof(Summary));
        SetProperty(sourceKey, value => SourceKey = value, SourceKey, nameof(SourceKey));
        SetProperty(sourceDisplayName, value => SourceDisplayName = value, SourceDisplayName, nameof(SourceDisplayName));
        SetProperty(statusText, value => StatusText = value, StatusText, nameof(StatusText));
        if (Status != status)
        {
            Status = status;
            OnPropertyChanged(nameof(Status));
        }
        SetProperty(accentBrush, value => AccentBrush = value, AccentBrush, nameof(AccentBrush));
        SetProperty(timeText, value => TimeText = value, TimeText, nameof(TimeText));
        UpdateInlineDetail(detailUserPrompt, detailAssistantReply);
    }

    private void SetProperty(string next, Action<string> assign, string current, string propertyName)
    {
        if (current == next)
            return;

        assign(next);
        OnPropertyChanged(propertyName);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
