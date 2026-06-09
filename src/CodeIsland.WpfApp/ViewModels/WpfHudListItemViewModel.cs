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

public sealed class WpfHudListItemViewModel
{
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
        ICommand openDetailCommand)
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
    public bool HasSession => !string.IsNullOrWhiteSpace(SessionId);
    public bool CanJumpToTerminal => Kind is WpfHudListItemKind.Running or WpfHudListItemKind.Completed && HasSession;
    public bool CanRemoveFromHudList => Kind is WpfHudListItemKind.Running or WpfHudListItemKind.Completed && HasSession;
    public bool HasSideActions => CanJumpToTerminal || CanRemoveFromHudList;
}
