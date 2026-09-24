using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodeIsland.WpfApp.Models;
using CodeIsland.WpfApp.Services;

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
    public string Title => _snapshot.ProjectName ?? _snapshot.WorkingDirectory ?? WpfSourceDisplay.GetDisplayName(_snapshot.Source, _snapshot.SourceDisplayName);
    public string SourceKey => _snapshot.Source;
    public string Source => WpfSourceDisplay.GetDisplayName(_snapshot.Source, _snapshot.SourceDisplayName);
    public string TimeText => LastUpdatedAt.ToString("HH:mm");
    public string StatusText
    {
        get
        {
            var text = _snapshot.Status switch
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

            if (_snapshot.BackgroundActive > 0)
                text += $" · 后台 {_snapshot.BackgroundActive}";

            if (FormatPermissionMode(_snapshot.PermissionMode) is { } mode)
                text += $" · {mode}";

            if (string.Equals(_snapshot.TurnOutcome, "failed", StringComparison.OrdinalIgnoreCase) &&
                _snapshot.Status is not (AgentStatus.Error or AgentStatus.WaitingApproval or AgentStatus.WaitingQuestion))
                text += " · 上轮失败";

            return text;
        }
    }
    public string ToolText
    {
        get
        {
            if (FormatCurrentTool(_snapshot) is { } tool)
                return tool;
            if (_snapshot.Status == AgentStatus.Processing)
                return "$ 思考中_";
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

    internal static string? FormatCurrentTool(SessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(session.CurrentToolDescription))
        {
            var name = session.CurrentToolName;
            var description = session.CurrentToolDescription.Trim();
            if (string.IsNullOrWhiteSpace(name) ||
                description.Contains(name, StringComparison.OrdinalIgnoreCase))
                return description.StartsWith('$') ? description : $"$ {description}";
            return $"$ {name}: {description}";
        }

        if (!string.IsNullOrWhiteSpace(session.CurrentToolName))
            return $"$ {session.CurrentToolName}";

        return null;
    }

    /// <summary>持续权限档位，不是一次待审批。未知值不显示。</summary>
    internal static string? FormatPermissionMode(string? mode) => mode switch
    {
        "default" => "默认权限",
        "acceptEdits" or "accept_edits" => "自动改",
        "bypassPermissions" or "bypass_permissions" => "跳过权限",
        "plan" => "计划模式",
        _ => null
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
