using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodeIsland.Core.Models;
using CodeIsland.Core.Services;

namespace CodeIsland.WpfApp.ViewModels;

public sealed class WpfSessionItemViewModel : INotifyPropertyChanged
{
    private readonly SettingsManager _settings;
    private SessionSnapshot _snapshot;

    public WpfSessionItemViewModel(SessionSnapshot snapshot, SettingsManager settings)
    {
        _snapshot = snapshot;
        _settings = settings;
    }

    public string SessionId => _snapshot.SessionId;
    public string Title => _snapshot.ProjectName ?? _snapshot.WorkingDirectory ?? SupportedSource.GetDisplayName(_snapshot.Source);
    public string SourceKey => _snapshot.Source;
    public string Source => SupportedSource.GetDisplayName(_snapshot.Source);
    public string TimeText => LastUpdatedAt.ToString("HH:mm");
    public string StatusText => _snapshot.Status switch
    {
        AgentStatus.Idle => "空闲",
        AgentStatus.Processing => "处理中",
        AgentStatus.Running => "运行中",
        AgentStatus.WaitingApproval => "等待审批",
        AgentStatus.WaitingQuestion => "等待回答",
        AgentStatus.Completed => "已完成",
        AgentStatus.Error => "错误",
        _ => "未知"
    };
    public string ToolText
    {
        get
        {
            if (_snapshot.Status == AgentStatus.Processing)
                return "$ 思考中_";
            if (!string.IsNullOrWhiteSpace(_snapshot.CurrentToolDescription))
                return $"$ {_snapshot.CurrentToolDescription}";
            if (!string.IsNullOrWhiteSpace(_snapshot.CurrentToolName))
                return $"$ {_snapshot.CurrentToolName}";
            return _snapshot.Status switch
            {
                AgentStatus.WaitingApproval => "$ 等待权限审批",
                AgentStatus.WaitingQuestion => "$ 等待你的回答",
                AgentStatus.Completed => "$ 会话已完成",
                AgentStatus.Error => "$ 需要关注错误",
                _ => "$ 就绪"
            };
        }
    }
    public string LastMessage => _snapshot.RecentMessages.LastOrDefault() is { } msg
        ? (msg.IsUser ? "你: " : $"{Source}: ") + FormatRecentMessage(msg.Text, 160)
        : FormatRecentMessage(_snapshot.CompletionText ?? _snapshot.LastAssistantMessage, 160, "暂无最近消息");
    public DateTime LastUpdatedAt => _snapshot.LastUpdatedAt.ToLocalTime();
    public AgentStatus Status => _snapshot.Status;

    public void Update(SessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(string.Empty);
    }

    public void RefreshDisplay()
    {
        OnPropertyChanged(string.Empty);
    }

    private string FormatRecentMessage(string? text, int maxPreviewLength, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        if (_settings.Get("show_full_recent_messages", false))
            return text;

        var value = text.Replace("\r", " ").Split('\n', 2)[0].Trim();
        return value.Length <= maxPreviewLength ? value : value[..maxPreviewLength] + "…";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
