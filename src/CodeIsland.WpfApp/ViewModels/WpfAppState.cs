using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using CodeIsland.Core.Models;
using CodeIsland.Core.Services;
using CodeIsland.WpfApp.Services;

namespace CodeIsland.WpfApp.ViewModels;

public enum WpfHudSurfaceKind
{
    Collapsed,
    SessionList,
    HudDetail,
    CompletionCard
}

public enum WpfPendingKind
{
    None,
    Permission,
    Question
}

public sealed class WpfAppState : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan SelectedSessionTranscriptRefreshInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan ProcessMonitorInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProcessStartTimeTolerance = TimeSpan.FromSeconds(2);
    private const string NeutralHudSource = "codeisland";

    private readonly SettingsManager _settings;
    private readonly EventLogger? _logger;
    private readonly WpfWebhookNotifier? _webhookNotifier;
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WpfSessionItemViewModel> _sessionItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedHudSessionIds = new(StringComparer.Ordinal);
    private readonly Queue<PendingPermission> _permissionQueue = new();
    private readonly Queue<PendingQuestion> _questionQueue = new();
    private WpfHudSurfaceKind _surfaceKind = WpfHudSurfaceKind.Collapsed;
    private string? _selectedSessionId;
    private string? _selectedHudItemId;
    private string? _selectedPendingActionId;
    private WpfHudListItemKind? _selectedPendingActionKind;
    private string? _completionSessionId;
    private int _pendingActionRevision;
    private System.Threading.Timer? _completionTimer;
    private System.Threading.Timer? _selectedSessionTranscriptRefreshTimer;
    private System.Threading.Timer? _processMonitorTimer;
    private string? _selectedSessionTranscriptRefreshSessionId;
    private bool _disposed;
    private readonly HashSet<string> _deferredSessionItemIds = new(StringComparer.Ordinal);
    private int _hudVisualUpdateDeferralDepth;
    private bool _hudVisualRefreshPending;
    private bool _hudQuestionOptionsRefreshPending;
    private bool _hudSessionItemsRefreshPending;

    public WpfAppState(SettingsManager settings, EventLogger? logger = null, WpfWebhookNotifier? webhookNotifier = null)
    {
        _settings = settings;
        _logger = logger;
        _webhookNotifier = webhookNotifier;
        ShowSessionListCommand = new RelayCommand(ShowSessionList);
        CollapseCommand = new RelayCommand(Collapse);
        DismissCompletionCommand = new RelayCommand(DismissCompletion);
        ApproveCommand = new RelayCommand(() => Approve(false));
        AlwaysApproveCommand = new RelayCommand(() => Approve(true));
        DenyCommand = new RelayCommand(Deny);
        DismissPermissionCommand = new RelayCommand(DismissPermission);
        DismissQuestionCommand = new RelayCommand(DismissQuestion);
        SubmitQuestionCommand = new RelayCommand(_ => SubmitQuestion(QuestionAnswer));
        SelectQuestionOptionCommand = new RelayCommand(parameter => HandleQuestionOption(parameter as WpfQuestionOptionViewModel));
        OpenSessionCommand = new RelayCommand(parameter => OpenSession(parameter as string));
        OpenHudListItemCommand = new RelayCommand(parameter => OpenHudListItem(parameter as string));
        RemoveHudListItemCommand = new RelayCommand(parameter => RemoveHudListItem(parameter as string));
        JumpToTerminalCommand = new RelayCommand(parameter => JumpToTerminal(parameter as string));
        SelectedSessionJumpToTerminalCommand = new RelayCommand(() => JumpToTerminal(_selectedSessionId));
        _settings.SettingChanged += OnSettingChanged;
        StartProcessMonitor();
    }

    public void BeginHudVisualUpdateDeferral()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(BeginHudVisualUpdateDeferral);
            return;
        }

        _hudVisualUpdateDeferralDepth++;
    }

    public void EndHudVisualUpdateDeferral()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(EndHudVisualUpdateDeferral);
            return;
        }

        if (_hudVisualUpdateDeferralDepth <= 0)
            return;

        _hudVisualUpdateDeferralDepth--;
        if (_hudVisualUpdateDeferralDepth > 0)
            return;

        FlushDeferredHudVisualUpdates();
    }

    public ObservableCollection<WpfSessionItemViewModel> Sessions { get; } = new();
    public ObservableCollection<WpfHudListItemViewModel> HudListItems { get; } = new();
    public ObservableCollection<WpfHudListGroupViewModel> HudListGroups { get; } = new();
    public WpfHudSurfaceKind SurfaceKind
    {
        get => _surfaceKind;
        private set
        {
            if (_surfaceKind == value)
                return;

            _surfaceKind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCollapsed));
            OnPropertyChanged(nameof(IsSessionList));
            OnPropertyChanged(nameof(IsHudDetail));
            OnPropertyChanged(nameof(IsCompletionCard));
            NotifyHudLayoutProperties();
        }
    }
    public bool IsCollapsed => SurfaceKind == WpfHudSurfaceKind.Collapsed;
    public bool IsSessionList => SurfaceKind == WpfHudSurfaceKind.SessionList;
    public bool IsHudDetail => SurfaceKind == WpfHudSurfaceKind.HudDetail;
    public bool IsCompletionCard => SurfaceKind == WpfHudSurfaceKind.CompletionCard;
    public bool HasSessions => VisibleHudSessionCount > 0;
    public bool HasNoSessions => VisibleHudSessionCount == 0;
    public bool HasHudListItems => HudListItems.Count > 0;
    public bool HasNoHudListItems => HudListItems.Count == 0;
    public int ActiveSessionCount => VisibleHudSessions.Count(static s => s.Status is AgentStatus.Processing or AgentStatus.Running or AgentStatus.WaitingApproval or AgentStatus.WaitingQuestion);
    public string SessionCountText => VisibleHudSessionCount == 0 ? "0/0" : $"{ActiveSessionCount}/{VisibleHudSessionCount}";
    public string ActiveSource => PrimaryHudSession?.Source ?? NeutralHudSource;
    public AgentStatus ActiveStatus => PrimaryHudSession?.Status ?? AgentStatus.Idle;
    public string ActiveStatusText => ActiveStatus switch
    {
        AgentStatus.Processing => "处理中",
        AgentStatus.Running => "运行中",
        AgentStatus.WaitingApproval => "等待审批",
        AgentStatus.WaitingQuestion => "等待回答",
        AgentStatus.Completed => "已完成",
        AgentStatus.Error => "错误",
        _ => "空闲"
    };
    public string CenterText => PrimaryHudSession?.ProjectName ?? PrimaryHudSession?.WorkingDirectory ?? (VisibleHudSessionCount == 0 ? "没有活跃会话" : $"{VisibleHudSessionCount} 个会话");
    public bool HasPendingAction => PendingKind != WpfPendingKind.None;
    public WpfPendingKind PendingKind => _permissionQueue.Count > 0 ? WpfPendingKind.Permission : _questionQueue.Count > 0 ? WpfPendingKind.Question : WpfPendingKind.None;
    public bool HasPendingPermission => PendingKind == WpfPendingKind.Permission;
    public bool HasPendingQuestion => PendingKind == WpfPendingKind.Question;
    public string PendingActionText => PendingKind switch { WpfPendingKind.Permission => "等待审批", WpfPendingKind.Question => "等待回答", _ => "" };
    public string PendingActionShortText => PendingKind switch { WpfPendingKind.Permission => "审批", WpfPendingKind.Question => "问答", _ => "" };
    public int PendingActionRevision => _pendingActionRevision;
    public bool IsSideCollapsed => IsCollapsed && WpfHudDisplayPosition.IsSideCenter(_settings.Get("display_position", WpfHudDisplayPosition.Default));
    public bool IsHorizontalCollapsed => !IsSideCollapsed;
    public bool IsCompactHudMode => WpfHudDensityMode.IsCompact(_settings.Get("hud_density_mode", WpfHudDensityMode.Default));
    public bool IsClassicHudMode => !IsCompactHudMode;
    public bool IsClassicHorizontalCollapsed => IsHorizontalCollapsed && IsClassicHudMode;
    public bool IsCompactHorizontalCollapsed => IsHorizontalCollapsed && IsCompactHudMode;
    public bool IsClassicSideCollapsed => IsSideCollapsed && IsClassicHudMode;
    public bool IsCompactSideCollapsed => IsSideCollapsed && IsCompactHudMode;
    public bool ShouldShowPendingAlert => HasPendingAction;
    public string? SelectedHudItemId => _selectedHudItemId;

    public string PermissionTitle => GetCurrentPermission()?.Request is { } p ? $"{p.ToolName} 请求权限" : "权限审批";
    public string PermissionDescription => GetCurrentPermission()?.Request is { } p ? BuildPermissionContent(p) : "";
    public string PermissionCommand => GetCurrentPermission()?.Request is { } p ? BuildPermissionContent(p) : "等待用户确认";
    public string PermissionProject => GetCurrentPermission() is { } p && _sessions.TryGetValue(p.Request.SessionId, out var s) ? s.ProjectName ?? s.WorkingDirectory ?? "未知项目" : "未知项目";
    public string PermissionSource => GetCurrentPermission() is { } p && _sessions.TryGetValue(p.Request.SessionId, out var s) ? SupportedSource.GetDisplayName(s.Source) : "未知工具";
    public string PermissionWorkDir => GetCurrentPermission() is { } p && _sessions.TryGetValue(p.Request.SessionId, out var s) ? s.WorkingDirectory ?? s.ProjectName ?? "未知路径" : "未知路径";
    public string QuestionTitle => GetCurrentQuestion() is { } q ? q.CurrentItem?.Header ?? q.Question.Header ?? "需要你的回答" : "需要你的回答";
    public string QuestionSource => GetCurrentQuestion() is { } q && _sessions.TryGetValue(q.Question.SessionId, out var s) ? SupportedSource.GetDisplayName(s.Source) : "未知工具";
    public string QuestionProject => GetCurrentQuestion() is { } q && _sessions.TryGetValue(q.Question.SessionId, out var s) ? s.ProjectName ?? s.WorkingDirectory ?? "未知项目" : "未知项目";
    public string QuestionText => GetCurrentQuestion() is { } q ? q.CurrentItem?.Question ?? q.Question.Question : "";
    public string QuestionHeader => GetCurrentQuestion() is { } q ? q.CurrentItem?.Header ?? q.Question.Header ?? "" : "";
    public bool HasQuestionHeader => !string.IsNullOrWhiteSpace(QuestionHeader);
    public string QuestionProgressText => GetCurrentQuestion() is { } q && q.Question.Questions is { Count: > 1 } questions ? $"第 {q.CurrentQuestionIndex + 1}/{questions.Count} 题" : "";
    public bool HasQuestionProgress => !string.IsNullOrWhiteSpace(QuestionProgressText);
    public bool HasQuestionOptions => QuestionOptions.Count > 0;
    public bool IsCurrentQuestionMultiSelect => GetCurrentQuestion() is { } q && q.CurrentMultiSelect;
    public bool HasSingleSelectOptions => HasQuestionOptions && !IsCurrentQuestionMultiSelect;
    public bool HasMultiSelectOptions => HasQuestionOptions && IsCurrentQuestionMultiSelect;
    public bool ShouldShowAnswerTextBox => !HasQuestionOptions;
    public bool ShouldShowQuestionSubmitButton => ShouldShowAnswerTextBox || HasMultiSelectOptions;
    public ObservableCollection<WpfQuestionOptionViewModel> QuestionOptions { get; } = new();
    public string QuestionAnswer { get => _questionAnswer; set { _questionAnswer = value; OnPropertyChanged(); } }
    private string _questionAnswer = "";

    public WpfSessionItemViewModel? SelectedSession => _selectedSessionId != null && _sessionItems.TryGetValue(_selectedSessionId, out var item) ? item : null;
    public WpfHudListItemViewModel? SelectedHudItem => _selectedHudItemId != null ? HudListItems.FirstOrDefault(item => item.ItemId == _selectedHudItemId) : null;
    public bool HasExpandedHudListSessionDetail => SurfaceKind == WpfHudSurfaceKind.SessionList && SelectedHudItem?.CanShowInlineSessionDetail == true;
    public bool IsSelectedPermissionDetail => SelectedHudItem?.Kind == WpfHudListItemKind.Permission;
    public bool IsSelectedQuestionDetail => SelectedHudItem?.Kind == WpfHudListItemKind.Question;
    public bool IsSelectedSessionDetail => !IsSelectedPermissionDetail && !IsSelectedQuestionDetail;
    public string DetailTitle => SelectedHudItem?.Title ?? SelectedSession?.Title ?? "任务详情";
    public string DetailSubtitle => SelectedHudItem == null
        ? (SelectedSession == null ? "请选择一个任务" : $"{SelectedSession.Source} · {SelectedSession.StatusText}")
        : $"{SelectedHudItem.SourceDisplayName} · {SelectedHudItem.ProjectName}";
    public string DetailStatusText => SelectedHudItem?.StatusText ?? SelectedSession?.StatusText ?? "未知";
    public string DetailToolText => SelectedSession?.ToolText ?? "$ 就绪";
    public string DetailUserPrompt => FormatRecentMessage(SelectedSnapshot?.LastUserPrompt, "暂无用户问题");
    public string DetailAssistantReplyTitle => $"{SelectedSession?.Source ?? "AI"} 回复";
    public string DetailAssistantReply => FormatRecentMessage(GetSelectedSessionAssistantReply(), $"暂无 {SelectedSession?.Source ?? "AI"} 回复");
    public string CompletionTitle => CompletionSession == null ? "回复已完成" : $"{SupportedSource.GetDisplayName(CompletionSession.Source)} 回复已完成";
    public string CompletionSource => CompletionSession == null ? "未知工具" : SupportedSource.GetDisplayName(CompletionSession.Source);
    public string CompletionProject => CompletionSession?.ProjectName ?? CompletionSession?.WorkingDirectory ?? "未知项目";
    public string CompletionUserPrompt => FormatRecentMessage(CompletionSession?.LastUserPrompt, "");
    public bool HasCompletionUserPrompt => !string.IsNullOrWhiteSpace(CompletionUserPrompt);
    public string CompletionText => FormatRecentMessage(CompletionSession?.CompletionText ?? CompletionSession?.LastAssistantMessage, "回复已完成");

    public ICommand ShowSessionListCommand { get; }
    public ICommand CollapseCommand { get; }
    public ICommand DismissCompletionCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand AlwaysApproveCommand { get; }
    public ICommand DenyCommand { get; }
    public ICommand DismissPermissionCommand { get; }
    public ICommand DismissQuestionCommand { get; }
    public ICommand SubmitQuestionCommand { get; }
    public ICommand SelectQuestionOptionCommand { get; }
    public ICommand OpenSessionCommand { get; }
    public ICommand OpenHudListItemCommand { get; }
    public ICommand RemoveHudListItemCommand { get; }
    public ICommand JumpToTerminalCommand { get; }
    public ICommand SelectedSessionJumpToTerminalCommand { get; }

    private SessionSnapshot? PrimaryHudSession => ResolvePrimaryHudSession();
    private SessionSnapshot? SelectedSnapshot => _selectedSessionId != null && _sessions.TryGetValue(_selectedSessionId, out var selected) ? selected : null;
    private SessionSnapshot? CompletionSession => _completionSessionId != null && _sessions.TryGetValue(_completionSessionId, out var s) ? s : null;
    private IEnumerable<SessionSnapshot> VisibleHudSessions => _sessions.Values.Where(IsVisibleHudSession);
    private int VisibleHudSessionCount => _sessions.Values.Count(IsVisibleHudSession);

    private SessionSnapshot? ResolvePrimaryHudSession()
    {
        if (ResolvePendingSession() is { } pendingSession)
            return pendingSession;

        if (_selectedSessionId != null &&
            _sessions.TryGetValue(_selectedSessionId, out var selectedSession) &&
            IsVisibleHudSession(selectedSession))
        {
            return selectedSession;
        }

        return VisibleHudSessions
            .OrderByDescending(static session => GetHudStatusPriority(session.Status))
            .ThenByDescending(static session => session.LastUpdatedAt)
            .FirstOrDefault();
    }

    private bool IsVisibleHudSession(SessionSnapshot session) => IsVisibleHudSession(session.SessionId);

    private bool IsVisibleHudSession(string sessionId) =>
        !_removedHudSessionIds.Contains(sessionId);

    private static bool ShouldRestoreRemovedHudSession(string normalizedEventName, AgentStatus status) =>
        normalizedEventName == "SessionStart" ||
        status is AgentStatus.Processing or AgentStatus.Running or AgentStatus.WaitingApproval or AgentStatus.WaitingQuestion or AgentStatus.Error;

    private SessionSnapshot? ResolvePendingSession()
    {
        foreach (var pending in _permissionQueue)
        {
            if (GetSession(pending.Request.SessionId) is { } session)
                return session;
        }

        foreach (var pending in _questionQueue)
        {
            if (GetSession(pending.Question.SessionId) is { } session)
                return session;
        }

        return null;
    }

    private static int GetHudStatusPriority(AgentStatus status) => status switch
    {
        AgentStatus.WaitingApproval => 70,
        AgentStatus.WaitingQuestion => 60,
        AgentStatus.Error => 55,
        AgentStatus.Processing => 50,
        AgentStatus.Running => 40,
        AgentStatus.Completed => 30,
        AgentStatus.Idle => 20,
        _ => 0
    };

    private string? GetSelectedSessionAssistantReply()
    {
        if (SelectedSnapshot is not { } session)
            return null;

        return GetSessionAssistantReply(session);
    }

    public async Task<string> HandleBlockingEventAsync(HookEvent evt, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        HandleEvent(evt, completion);
        try
        {
            var seconds = Math.Clamp(_settings.Get("session_timeout", 300), 30, 3600);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(seconds), ct);
        }
        catch (TimeoutException)
        {
            RemoveCompletion(completion);
            RefreshAll();
            return IsQuestionEvent(evt) ? BuildQuestionDismissResponse(evt, "timeout") : BuildPermissionDenyResponse(evt, "timeout");
        }
    }

    public void HandleEvent(HookEvent evt) => HandleEvent(evt, null);

    private void HandleEvent(HookEvent evt, TaskCompletionSource<string>? blockingCompletion)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var sessionId = ResolveSessionId(evt);
            var existing = sessionId != null && _sessions.TryGetValue(sessionId, out var s) ? s : null;
            var (newState, effect) = SessionSnapshot.ReduceEvent(existing, evt);
            newState = ApplyTranscriptMessages(existing, newState);
            sessionId = newState.SessionId;

            var normalizedEventName = EventNormalizer.NormalizeEventName(newState.Source, evt.EventName);
            if (ShouldRestoreRemovedHudSession(normalizedEventName, newState.Status))
                _removedHudSessionIds.Remove(sessionId);

            if (normalizedEventName == "Stop" && effect is SideEffect.None && HasCompletionContent(newState))
                effect = new SideEffect.PlaySound("complete");

            if (normalizedEventName == "SessionEnd")
            {
                RemoveSession(sessionId, "session ended");
            }
            else
            {
                _sessions[sessionId] = newState;
                SyncSessionItem(sessionId, newState);
                _selectedSessionId ??= sessionId;
            }

            _webhookNotifier?.NotifySessionChanged(newState);

            switch (effect)
            {
                case SideEffect.ShowApprovalCard approval when blockingCompletion != null:
                    if (ShouldAutoApprove(approval.Request))
                    {
                        blockingCompletion.TrySetResult(BuildPermissionAllowResponse(evt, approval.Request, always: false));
                    }
                    else
                    {
                        _permissionQueue.Enqueue(new PendingPermission(NewPendingActionId("permission"), DateTime.UtcNow, approval.Request, blockingCompletion, evt));
                        _pendingActionRevision++;
                        ClearCompletionCardForPendingAction();
                        _webhookNotifier?.NotifyApproval(approval.Request);
                    }
                    break;
                case SideEffect.ShowQuestionCard question when blockingCompletion != null:
                    _questionQueue.Enqueue(new PendingQuestion(NewPendingActionId("question"), DateTime.UtcNow, question.Question, blockingCompletion, evt));
                    _pendingActionRevision++;
                    ClearCompletionCardForPendingAction();
                    _webhookNotifier?.NotifyQuestion(question.Question);
                    QuestionAnswer = "";
                    RefreshQuestionOptions();
                    break;
                case SideEffect.PlaySound ps when IsVisibleHudSession(sessionId):
                    PlaySoundRequested?.Invoke(ps.SoundName);
                    if (ps.SoundName == "complete")
                        ShowCompletion(sessionId);
                    break;
            }

            if (effect is SideEffect.None && TryUpdateExpandedInlineSessionItemInPlace(sessionId, newState))
            {
                UpdateSelectedSessionTranscriptRefresh();
                return;
            }

            RefreshAll();
        });
    }

    public void ShowSessionList()
    {
        _selectedHudItemId = null;
        _selectedPendingActionId = null;
        _selectedPendingActionKind = null;
        SurfaceKind = WpfHudSurfaceKind.SessionList;
        RefreshAll();
    }

    public void OpenSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        OpenInlineSessionDetail($"session:{sessionId}", sessionId);
    }

    private void OpenHudListItem(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        var item = HudListItems.FirstOrDefault(candidate => candidate.ItemId == itemId);
        if (item == null) return;

        if (item.CanShowInlineSessionDetail)
        {
            OpenInlineSessionDetail(item.ItemId, item.SessionId);
            return;
        }

        _selectedHudItemId = item.ItemId;
        _selectedSessionId = item.SessionId;
        _selectedPendingActionId = item.Kind is WpfHudListItemKind.Permission or WpfHudListItemKind.Question
            ? item.ItemId.Split(':').LastOrDefault()
            : null;
        _selectedPendingActionKind = item.Kind is WpfHudListItemKind.Permission or WpfHudListItemKind.Question
            ? item.Kind
            : null;
        SurfaceKind = WpfHudSurfaceKind.HudDetail;
        QuestionAnswer = "";
        RefreshQuestionOptions();
        RefreshAll();
    }

    private void OpenInlineSessionDetail(string itemId, string? sessionId)
    {
        var collapseCurrent = SurfaceKind == WpfHudSurfaceKind.SessionList &&
            string.Equals(_selectedHudItemId, itemId, StringComparison.Ordinal);

        _selectedHudItemId = collapseCurrent ? null : itemId;
        _selectedSessionId = sessionId;
        _selectedPendingActionId = null;
        _selectedPendingActionKind = null;
        SurfaceKind = WpfHudSurfaceKind.SessionList;
        UpdateExpandedHudListItems();
        RefreshQuestionOptions();
        OnPropertyChanged(nameof(SelectedHudItemId));
        OnPropertyChanged(nameof(SelectedHudItem));
        OnPropertyChanged(nameof(HasExpandedHudListSessionDetail));
        UpdateSelectedSessionTranscriptRefresh();
    }

    private void RemoveHudListItem(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        var item = HudListItems.FirstOrDefault(candidate => candidate.ItemId == itemId);
        if (item is not { CanRemoveFromHudList: true } || string.IsNullOrWhiteSpace(item.SessionId))
            return;

        if (!_removedHudSessionIds.Add(item.SessionId))
            return;

        if (string.Equals(_selectedHudItemId, item.ItemId, StringComparison.Ordinal))
            _selectedHudItemId = null;
        if (string.Equals(_selectedSessionId, item.SessionId, StringComparison.Ordinal))
            _selectedSessionId = null;

        if (SurfaceKind == WpfHudSurfaceKind.HudDetail && _selectedSessionId == null && _selectedPendingActionId == null)
            SurfaceKind = WpfHudSurfaceKind.SessionList;

        RefreshQuestionOptions();
        RefreshAll();
    }

    public void JumpToTerminal(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
            return;

        _ = WpfTerminalActivator.Activate(session);
    }

    public void Collapse()
    {
        _selectedHudItemId = null;
        _selectedPendingActionId = null;
        _selectedPendingActionKind = null;
        UpdateExpandedHudListItems();
        SurfaceKind = WpfHudSurfaceKind.Collapsed;
        RefreshAll();
    }

    private void ShowCompletion(string sessionId)
    {
        if (!IsVisibleHudSession(sessionId))
            return;

        if (HasPendingAction || SurfaceKind is WpfHudSurfaceKind.SessionList or WpfHudSurfaceKind.HudDetail)
            return;

        if (!_sessions.TryGetValue(sessionId, out var session) || !HasCompletionContent(session))
        {
            return;
        }

        _completionSessionId = sessionId;
        SurfaceKind = WpfHudSurfaceKind.CompletionCard;
        _completionTimer?.Dispose();
        _completionTimer = new System.Threading.Timer(_ => System.Windows.Application.Current.Dispatcher.Invoke(DismissCompletion), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    private void ClearCompletionCardForPendingAction()
    {
        if (SurfaceKind != WpfHudSurfaceKind.CompletionCard)
            return;

        _completionTimer?.Dispose();
        _completionTimer = null;
        _completionSessionId = null;
        SurfaceKind = WpfHudSurfaceKind.Collapsed;
    }

    public void DismissCompletion()
    {
        _completionTimer?.Dispose();
        _completionTimer = null;
        _completionSessionId = null;
        SurfaceKind = WpfHudSurfaceKind.Collapsed;
        RefreshAll();
    }

    public void PauseCompletionAutoCollapse()
    {
        if (SurfaceKind != WpfHudSurfaceKind.CompletionCard)
            return;

        _completionTimer?.Dispose();
        _completionTimer = null;
    }

    public void ResumeCompletionAutoCollapse()
    {
        if (SurfaceKind != WpfHudSurfaceKind.CompletionCard || string.IsNullOrWhiteSpace(_completionSessionId))
            return;

        _completionTimer?.Dispose();
        _completionTimer = new System.Threading.Timer(_ => System.Windows.Application.Current.Dispatcher.Invoke(DismissCompletion), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    public void Approve(bool always)
    {
        var pending = RemoveSelectedOrHeadPermission();
        if (pending == null) return;
        pending.Completion.TrySetResult(BuildPermissionAllowResponse(pending.Event, pending.Request, always));
        AdvanceAfterPendingResponse();
    }

    public void Deny()
    {
        var pending = RemoveSelectedOrHeadPermission();
        if (pending == null) return;
        pending.Completion.TrySetResult(BuildPermissionDenyResponse(pending.Event, "user denied"));
        AdvanceAfterPendingResponse();
    }

    public void DismissPermission()
    {
        var pending = RemoveSelectedOrHeadPermission();
        if (pending == null) return;
        pending.Completion.TrySetResult(BuildPermissionDenyResponse(pending.Event, "dismissed"));
        AdvanceAfterPendingResponse();
    }

    public void DismissQuestion()
    {
        var pending = RemoveSelectedOrHeadQuestion();
        if (pending == null) return;
        pending.Completion.TrySetResult(BuildQuestionDismissResponse(pending.Event, "dismissed"));
        pending.ClearAnswerState();
        QuestionAnswer = "";
        RefreshQuestionOptions();
        AdvanceAfterPendingResponse();
    }

    public void SubmitQuestion(string answer)
    {
        var pending = GetCurrentQuestion();
        if (pending == null) return;
        var resolvedAnswers = pending.CurrentMultiSelect
            ? QuestionOptions.Where(static option => option.IsSelected).Select(static option => option.ResponseValue).ToArray()
            : [answer];
        if (!TryRecordQuestionAnswer(pending, resolvedAnswers)) return;
        if (AdvanceQuestionIfNeeded(pending)) return;

        RemoveQuestionById(pending.ActionId);
        pending.Completion.TrySetResult(BuildQuestionAnswerResponse(pending));
        pending.ClearAnswerState();
        QuestionAnswer = "";
        RefreshQuestionOptions();
        AdvanceAfterPendingResponse();
    }

    private void HandleQuestionOption(WpfQuestionOptionViewModel? option)
    {
        var pending = GetCurrentQuestion();
        if (option == null || pending == null) return;
        if (pending.CurrentMultiSelect)
            return;

        SubmitQuestionOption(option);
    }

    private void SubmitQuestionOption(WpfQuestionOptionViewModel? option)
    {
        var pending = GetCurrentQuestion();
        if (option == null || pending == null) return;
        if (!TryRecordQuestionAnswer(pending, [option.ResponseValue])) return;
        if (AdvanceQuestionIfNeeded(pending)) return;

        RemoveQuestionById(pending.ActionId);
        pending.Completion.TrySetResult(BuildQuestionAnswerResponse(pending));
        pending.ClearAnswerState();
        QuestionAnswer = "";
        RefreshQuestionOptions();
        AdvanceAfterPendingResponse();
    }

    private bool TryRecordQuestionAnswer(PendingQuestion pending, IReadOnlyList<string> answers)
    {
        var cleanedAnswers = answers
            .Where(static answer => !string.IsNullOrWhiteSpace(answer))
            .Select(static answer => answer.Trim())
            .ToArray();
        if (cleanedAnswers.Length == 0) return false;

        var key = pending.CurrentAnswerKey;
        if (string.IsNullOrWhiteSpace(key)) key = "answer";
        pending.Answers[key] = cleanedAnswers;
        return true;
    }

    private bool AdvanceQuestionIfNeeded(PendingQuestion pending)
    {
        if (pending.Question.Questions is not { Count: > 0 } questions || pending.CurrentQuestionIndex >= questions.Count - 1)
            return false;

        pending.CurrentQuestionIndex++;
        QuestionAnswer = "";
        RefreshQuestionOptions();
        RefreshAll();
        return true;
    }

    private void RefreshQuestionOptions()
    {
        if (IsHudVisualUpdateDeferred)
        {
            _hudQuestionOptionsRefreshPending = true;
            return;
        }

        RefreshQuestionOptionsCore();
    }

    private void RefreshQuestionOptionsCore()
    {
        QuestionOptions.Clear();
        if (GetCurrentQuestion() is { } pending)
        {
            foreach (var option in pending.CurrentOptions)
                QuestionOptions.Add(new WpfQuestionOptionViewModel(option));
        }
        OnPropertyChanged(nameof(QuestionOptions));
        OnPropertyChanged(nameof(HasQuestionOptions));
        OnPropertyChanged(nameof(IsCurrentQuestionMultiSelect));
        OnPropertyChanged(nameof(HasSingleSelectOptions));
        OnPropertyChanged(nameof(HasMultiSelectOptions));
        OnPropertyChanged(nameof(ShouldShowAnswerTextBox));
        OnPropertyChanged(nameof(ShouldShowQuestionSubmitButton));
    }

    private PendingPermission? GetCurrentPermission()
    {
        if (_selectedPendingActionId == null || SurfaceKind != WpfHudSurfaceKind.HudDetail)
            return _permissionQueue.FirstOrDefault();

        return _selectedPendingActionKind == WpfHudListItemKind.Permission
            ? _permissionQueue.FirstOrDefault(pending => pending.ActionId == _selectedPendingActionId)
            : null;
    }

    private PendingQuestion? GetCurrentQuestion()
    {
        if (_selectedPendingActionId == null || SurfaceKind != WpfHudSurfaceKind.HudDetail)
            return _questionQueue.FirstOrDefault();

        return _selectedPendingActionKind == WpfHudListItemKind.Question
            ? _questionQueue.FirstOrDefault(pending => pending.ActionId == _selectedPendingActionId)
            : null;
    }

    private PendingPermission? RemoveSelectedOrHeadPermission()
    {
        var pending = GetCurrentPermission();
        if (pending == null) return null;
        return RemovePermissionById(pending.ActionId) ? pending : null;
    }

    private PendingQuestion? RemoveSelectedOrHeadQuestion()
    {
        var pending = GetCurrentQuestion();
        if (pending == null) return null;
        return RemoveQuestionById(pending.ActionId) ? pending : null;
    }

    private bool RemovePermissionById(string actionId)
    {
        var removed = RemoveMatching(_permissionQueue, pending => pending.ActionId == actionId) > 0;
        if (removed)
            _pendingActionRevision++;
        return removed;
    }

    private bool RemoveQuestionById(string actionId)
    {
        var removed = RemoveMatching(_questionQueue, pending => pending.ActionId == actionId) > 0;
        if (removed)
            _pendingActionRevision++;
        return removed;
    }

    private static int RemoveMatching<T>(Queue<T> queue, Func<T, bool> predicate)
    {
        var removed = 0;
        var retained = new List<T>(queue.Count);
        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (predicate(item))
                removed++;
            else
                retained.Add(item);
        }

        foreach (var item in retained)
            queue.Enqueue(item);

        return removed;
    }

    private void AdvanceAfterPendingResponse()
    {
        _selectedPendingActionId = null;
        _selectedPendingActionKind = null;
        RebuildHudListItems();

        if (SurfaceKind == WpfHudSurfaceKind.HudDetail)
        {
            var nextPending = HudListItems.FirstOrDefault(static item => item.Kind is WpfHudListItemKind.Permission or WpfHudListItemKind.Question);
            if (nextPending != null)
            {
                OpenHudListItem(nextPending.ItemId);
                return;
            }

            ShowSessionList();
            return;
        }

        if (!HasPendingAction && SurfaceKind != WpfHudSurfaceKind.SessionList)
            SurfaceKind = WpfHudSurfaceKind.Collapsed;
        RefreshQuestionOptions();
        RefreshAll();
    }

    private string? ResolveSessionId(HookEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.SessionId)) return evt.SessionId;
        if (_sessions.Count == 1) return _sessions.Keys.First();
        if (evt.TrackedPid is { } pid)
        {
            var matches = _sessions.Values.Where(s => s.Pid == pid).ToArray();
            if (matches.Length == 1) return matches[0].SessionId;
        }
        return null;
    }

    private void StartProcessMonitor()
    {
        _processMonitorTimer = new System.Threading.Timer(_ =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (_disposed || dispatcher == null)
                return;

            dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                    RemoveExitedSessions();
            });
        }, null, ProcessMonitorInterval, ProcessMonitorInterval);
    }

    private void RemoveExitedSessions()
    {
        if (_sessions.Count == 0)
            return;

        var exitedSessionIds = _sessions.Values
            .Where(IsTrackedProcessExited)
            .Select(static session => session.SessionId)
            .ToArray();
        if (exitedSessionIds.Length == 0)
            return;

        foreach (var sessionId in exitedSessionIds)
            RemoveSession(sessionId, "process exited");

        RefreshQuestionOptions();
        RefreshAll();
    }

    private static bool IsTrackedProcessExited(SessionSnapshot session)
    {
        if (session.Pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(session.Pid);
            if (process.HasExited)
                return true;

            if (session.TrackedProcessStartedAtUtc is not { } expectedStart)
                return false;

            if (!TryGetProcessStartTimeUtc(process, out var actualStart))
                return false;

            return (actualStart - expectedStart).Duration() > ProcessStartTimeTolerance;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasCompletionContent(SessionSnapshot session) =>
        !string.IsNullOrWhiteSpace(session.CompletionText) ||
        !string.IsNullOrWhiteSpace(session.LastAssistantMessage);

    private static bool TryGetProcessStartTimeUtc(Process process, out DateTime startedAtUtc)
    {
        try
        {
            startedAtUtc = process.StartTime.ToUniversalTime();
            return true;
        }
        catch
        {
            startedAtUtc = default;
            return false;
        }
    }

    private bool RemoveSession(string sessionId, string pendingResponseReason)
    {
        var removed = _sessions.Remove(sessionId);
        removed = _removedHudSessionIds.Remove(sessionId) || removed;
        if (_sessionItems.ContainsKey(sessionId))
        {
            if (IsHudVisualUpdateDeferred)
            {
                _deferredSessionItemIds.Add(sessionId);
                _hudSessionItemsRefreshPending = true;
                removed = true;
            }
            else if (RemoveSessionItemCore(sessionId))
            {
                removed = true;
            }
        }

        if (RemovePendingActionsForSession(sessionId, pendingResponseReason))
            removed = true;

        if (_selectedSessionId == sessionId)
        {
            _selectedSessionId = null;
            _selectedHudItemId = null;
            removed = true;
        }

        if (_completionSessionId == sessionId)
        {
            _completionTimer?.Dispose();
            _completionTimer = null;
            _completionSessionId = null;
            if (SurfaceKind == WpfHudSurfaceKind.CompletionCard)
                SurfaceKind = WpfHudSurfaceKind.Collapsed;
        }

        if (SurfaceKind == WpfHudSurfaceKind.HudDetail && _selectedSessionId == null && _selectedPendingActionId == null)
            SurfaceKind = HasPendingAction || HasSessions ? WpfHudSurfaceKind.SessionList : WpfHudSurfaceKind.Collapsed;

        StopSelectedSessionTranscriptRefresh();
        return removed;
    }

    private bool RemovePendingActionsForSession(string sessionId, string reason)
    {
        var removed = false;
        var permissions = _permissionQueue.ToArray();
        _permissionQueue.Clear();
        foreach (var pending in permissions)
        {
            if (string.Equals(pending.Request.SessionId, sessionId, StringComparison.Ordinal))
            {
                pending.Completion.TrySetResult(BuildPermissionDenyResponse(pending.Event, reason));
                ClearSelectedPendingIfMatches(pending.ActionId, WpfHudListItemKind.Permission);
                removed = true;
            }
            else
            {
                _permissionQueue.Enqueue(pending);
            }
        }

        var questions = _questionQueue.ToArray();
        _questionQueue.Clear();
        foreach (var pending in questions)
        {
            if (string.Equals(pending.Question.SessionId, sessionId, StringComparison.Ordinal))
            {
                pending.Completion.TrySetResult(BuildQuestionDismissResponse(pending.Event, reason));
                ClearSelectedPendingIfMatches(pending.ActionId, WpfHudListItemKind.Question);
                removed = true;
            }
            else
            {
                _questionQueue.Enqueue(pending);
            }
        }

        if (removed)
            _pendingActionRevision++;
        return removed;
    }

    private void ClearSelectedPendingIfMatches(string actionId, WpfHudListItemKind kind)
    {
        if (!string.Equals(_selectedPendingActionId, actionId, StringComparison.Ordinal) || _selectedPendingActionKind != kind)
            return;

        _selectedHudItemId = null;
        _selectedPendingActionId = null;
        _selectedPendingActionKind = null;
    }

    private static bool IsQuestionEvent(HookEvent evt)
    {
        var name = EventNormalizer.NormalizeEventName(evt.Source ?? "unknown", evt.EventName);
        if (HookToolClassifier.ShouldBlockQuestionTool(evt, name)) return true;
        if (!name.StartsWith("Question", StringComparison.OrdinalIgnoreCase) && name != "Notification") return false;
        return ContainsAny(evt.RawJson, "question", "questions") || ContainsAny(evt.ToolInput, "question", "questions");
    }

    private static bool ContainsAny(JsonElement? element, params string[] names)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return false;
        foreach (var property in obj.EnumerateObject())
        {
            if (names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase))) return true;
            if (property.Value.ValueKind == JsonValueKind.Object && ContainsAny(property.Value, names)) return true;
        }
        return false;
    }

    private bool ShouldAutoApprove(PermissionRequest request)
    {
        if (!_settings.Get("auto_approve_safe_tools", false))
            return false;

        return IsSafeReadOnlyPermissionRequest(request);
    }

    private void RebuildHudListItems()
    {
        var pendingSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<WpfHudListItemViewModel>();

        foreach (var pending in _permissionQueue)
        {
            var session = GetSession(pending.Request.SessionId);
            if (!string.IsNullOrWhiteSpace(pending.Request.SessionId))
                pendingSessionIds.Add(pending.Request.SessionId);

            items.Add(new WpfHudListItemViewModel(
                $"permission:{pending.ActionId}",
                WpfHudListItemKind.Permission,
                pending.Request.SessionId,
                "权限请求",
                GetSessionProjectName(session),
                BuildPermissionSummary(pending.Request),
                GetSessionSourceKey(session),
                GetSessionSourceDisplayName(session),
                "等待审批",
                AgentStatus.WaitingApproval,
                "#FFFFB86B",
                FormatAge(pending.CreatedAt),
                OpenHudListItemCommand));
        }

        foreach (var pending in _questionQueue)
        {
            var session = GetSession(pending.Question.SessionId);
            if (!string.IsNullOrWhiteSpace(pending.Question.SessionId))
                pendingSessionIds.Add(pending.Question.SessionId);

            items.Add(new WpfHudListItemViewModel(
                $"question:{pending.ActionId}",
                WpfHudListItemKind.Question,
                pending.Question.SessionId,
                "问答请求",
                GetSessionProjectName(session),
                pending.CurrentQuestionText,
                GetSessionSourceKey(session),
                GetSessionSourceDisplayName(session),
                "等待回答",
                AgentStatus.WaitingQuestion,
                "#FF7AB8FF",
                FormatAge(pending.CreatedAt),
                OpenHudListItemCommand));
        }

        foreach (var vm in _sessionItems.Values)
        {
            if (pendingSessionIds.Contains(vm.SessionId))
                continue;
            if (_removedHudSessionIds.Contains(vm.SessionId))
                continue;

            var session = GetSession(vm.SessionId);
            var kind = GetHudSessionListItemKind(session);

            items.Add(new WpfHudListItemViewModel(
                $"session:{vm.SessionId}",
                kind,
                vm.SessionId,
                kind == WpfHudListItemKind.Completed ? "已完成" : "运行中",
                vm.Title,
                vm.LastMessage,
                vm.SourceKey,
                vm.Source,
                vm.StatusText,
                vm.Status,
                kind == WpfHudListItemKind.Completed ? "#FF8EE6D0" : "#FF7AB8FF",
                vm.TimeText,
                OpenHudListItemCommand,
                FormatSessionUserPrompt(session),
                FormatSessionAssistantReply(session),
                SurfaceKind == WpfHudSurfaceKind.SessionList && string.Equals(_selectedHudItemId, $"session:{vm.SessionId}", StringComparison.Ordinal)));
        }

        var sortedItems = items
            .OrderBy(static item => item.Kind switch
            {
                WpfHudListItemKind.Permission => 0,
                WpfHudListItemKind.Question => 1,
                WpfHudListItemKind.Running => 2,
                _ => 3
            })
            .ThenBy(static item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        HudListItems.Clear();
        foreach (var item in sortedItems)
            HudListItems.Add(item);

        RebuildHudListGroups(sortedItems);
        UpdateExpandedHudListItems();
    }

    private void RebuildHudListGroups(IReadOnlyList<WpfHudListItemViewModel> sortedItems)
    {
        var groupItems = new Dictionary<string, List<WpfHudListItemViewModel>>(StringComparer.OrdinalIgnoreCase);
        var sourceOrder = new List<string>();

        foreach (var item in sortedItems)
        {
            var sourceKey = string.IsNullOrWhiteSpace(item.SourceKey) ? "unknown" : item.SourceKey;
            if (!groupItems.TryGetValue(sourceKey, out var items))
            {
                items = new List<WpfHudListItemViewModel>();
                groupItems[sourceKey] = items;
                sourceOrder.Add(sourceKey);
            }

            items.Add(item);
        }

        HudListGroups.Clear();
        foreach (var sourceKey in sourceOrder)
            HudListGroups.Add(new WpfHudListGroupViewModel(sourceKey, groupItems[sourceKey]));
    }

    private SessionSnapshot? GetSession(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out var session) ? session : null;

    private static string GetSessionProjectName(SessionSnapshot? session) =>
        session?.ProjectName ?? session?.WorkingDirectory ?? "未知项目";

    private static string GetSessionSourceDisplayName(SessionSnapshot? session) =>
        session != null ? SupportedSource.GetDisplayName(session.Source) : "未知工具";

    private static string GetSessionSourceKey(SessionSnapshot? session) =>
        string.IsNullOrWhiteSpace(session?.Source) ? "unknown" : session.Source;

    private static string FormatAge(DateTime createdAt)
    {
        var age = DateTime.UtcNow - createdAt;
        if (age.TotalHours >= 1) return $"等待 {(int)age.TotalHours}h";
        if (age.TotalMinutes >= 1) return $"等待 {(int)age.TotalMinutes}m";
        return $"等待 {Math.Max(0, (int)age.TotalSeconds)}s";
    }

    private string FormatSessionUserPrompt(SessionSnapshot? session) =>
        FormatRecentMessage(session?.LastUserPrompt, "暂无用户问题");

    private string FormatSessionAssistantReply(SessionSnapshot? session)
    {
        var source = session == null ? "AI" : SupportedSource.GetDisplayName(session.Source);
        return FormatRecentMessage(GetSessionAssistantReply(session), $"暂无 {source} 回复");
    }

    private static string? GetSessionAssistantReply(SessionSnapshot? session)
    {
        if (session == null)
            return null;

        return session.Status is AgentStatus.Processing or AgentStatus.Running
            ? session.LastAssistantMessage ?? session.CompletionText
            : session.CompletionText ?? session.LastAssistantMessage;
    }

    private void UpdateExpandedHudListItems()
    {
        foreach (var item in HudListItems)
        {
            item.IsExpanded = SurfaceKind == WpfHudSurfaceKind.SessionList &&
                item.CanShowInlineSessionDetail &&
                string.Equals(item.ItemId, _selectedHudItemId, StringComparison.Ordinal);
        }
    }

    private bool TryUpdateExpandedInlineSessionDetail(string sessionId, SessionSnapshot session)
    {
        if (SurfaceKind != WpfHudSurfaceKind.SessionList ||
            !string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal) ||
            SelectedHudItem is not { CanShowInlineSessionDetail: true } selectedItem)
        {
            return false;
        }

        selectedItem.UpdateInlineDetail(FormatSessionUserPrompt(session), FormatSessionAssistantReply(session));
        return true;
    }

    private bool TryUpdateExpandedInlineSessionItemInPlace(string sessionId, SessionSnapshot session)
    {
        if (SurfaceKind != WpfHudSurfaceKind.SessionList ||
            !string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal) ||
            SelectedHudItem is not { CanShowInlineSessionDetail: true } selectedItem ||
            !_sessionItems.TryGetValue(sessionId, out var sessionItem))
        {
            return false;
        }

        var kind = GetHudSessionListItemKind(session);
        if (selectedItem.Kind != kind ||
            !string.Equals(selectedItem.SourceKey, sessionItem.SourceKey, StringComparison.Ordinal) ||
            !string.Equals(selectedItem.ProjectName, sessionItem.Title, StringComparison.Ordinal))
        {
            return false;
        }

        selectedItem.UpdateSessionPresentation(
            kind == WpfHudListItemKind.Completed ? "已完成" : "运行中",
            sessionItem.Title,
            sessionItem.LastMessage,
            sessionItem.SourceKey,
            sessionItem.Source,
            sessionItem.StatusText,
            sessionItem.Status,
            kind == WpfHudListItemKind.Completed ? "#FF8EE6D0" : "#FF7AB8FF",
            sessionItem.TimeText,
            FormatSessionUserPrompt(session),
            FormatSessionAssistantReply(session));
        return true;
    }

    private static string BuildPermissionSummary(PermissionRequest request)
    {
        if (request.ToolInput != null && request.ToolInput.TryGetValue("command", out var cmd) && cmd is string s && !string.IsNullOrWhiteSpace(s))
            return $"$ {s}";
        if (request.ToolInput != null && request.ToolInput.TryGetValue("pattern", out var pat) && pat is string p && !string.IsNullOrWhiteSpace(p))
            return $"{request.ToolName} \"{p}\"";
        var content = BuildPermissionContent(request);
        return content == "等待用户确认" ? request.ToolName ?? "权限请求" : content;
    }

    private static string NewPendingActionId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private void SyncSessionItem(string sessionId, SessionSnapshot snapshot)
    {
        if (IsHudVisualUpdateDeferred)
        {
            _deferredSessionItemIds.Add(sessionId);
            _hudSessionItemsRefreshPending = true;
            return;
        }

        SyncSessionItemCore(sessionId, snapshot);
    }

    private void SyncSessionItemCore(string sessionId, SessionSnapshot snapshot)
    {
        if (_sessionItems.TryGetValue(sessionId, out var item))
        {
            item.Update(snapshot);
        }
        else
        {
            item = new WpfSessionItemViewModel(snapshot, _settings);
            _sessionItems[sessionId] = item;
            Sessions.Insert(0, item);
        }
    }

    private void SyncDeferredSessionItems()
    {
        foreach (var sessionId in _deferredSessionItemIds.ToArray())
        {
            if (_sessions.TryGetValue(sessionId, out var snapshot))
                SyncSessionItemCore(sessionId, snapshot);
            else
                RemoveSessionItemCore(sessionId);
        }

        _deferredSessionItemIds.Clear();
    }

    private bool RemoveSessionItemCore(string sessionId)
    {
        if (!_sessionItems.Remove(sessionId, out var removedItem))
            return false;

        Sessions.Remove(removedItem);
        return true;
    }

    private static SessionSnapshot ApplyTranscriptMessages(SessionSnapshot? existing, SessionSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.TranscriptPath))
            return snapshot;

        var startPosition = existing?.TranscriptPath == snapshot.TranscriptPath
            ? existing.TranscriptPosition
            : 0;
        var result = TranscriptMessageReader.ReadNewMessages(snapshot.TranscriptPath, startPosition);
        if (result.Position == startPosition && result.Messages.Count == 0)
            return snapshot;

        var clone = snapshot.Clone();
        clone.TranscriptPosition = result.Position;
        foreach (var message in result.Messages)
            SessionSnapshot.AddRecentMessage(clone, message);

        clone.CompletionText ??= clone.LastAssistantMessage;
        return clone;
    }

    public bool RefreshSelectedSessionTranscriptForCurrentDetail()
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            return System.Windows.Application.Current.Dispatcher.Invoke(RefreshSelectedSessionTranscriptForCurrentDetail);

        if (!TryGetSelectedSessionForTranscriptRefresh(out var sessionId, out var session))
            return false;

        var refreshed = ApplyTranscriptMessages(session, session);
        if (ReferenceEquals(refreshed, session))
            return false;

        _sessions[sessionId] = refreshed;
        SyncSessionItem(sessionId, refreshed);
        if (TryUpdateExpandedInlineSessionItemInPlace(sessionId, refreshed) ||
            TryUpdateExpandedInlineSessionDetail(sessionId, refreshed))
        {
            UpdateSelectedSessionTranscriptRefresh();
            return true;
        }

        RefreshAll();
        return true;
    }

    private bool TryGetSelectedSessionForTranscriptRefresh(out string sessionId, out SessionSnapshot session)
    {
        sessionId = "";
        session = null!;

        var selectedSessionDetailVisible =
            (SurfaceKind == WpfHudSurfaceKind.HudDetail && IsSelectedSessionDetail) ||
            (SurfaceKind == WpfHudSurfaceKind.SessionList && SelectedHudItem?.CanShowInlineSessionDetail == true);
        if (!selectedSessionDetailVisible || string.IsNullOrWhiteSpace(_selectedSessionId))
            return false;

        if (!_sessions.TryGetValue(_selectedSessionId, out var selectedSession))
            return false;

        if (selectedSession.Status is not (AgentStatus.Processing or AgentStatus.Running))
            return false;

        if (string.IsNullOrWhiteSpace(selectedSession.TranscriptPath))
            return false;

        sessionId = _selectedSessionId;
        session = selectedSession;
        return true;
    }

    private void UpdateSelectedSessionTranscriptRefresh()
    {
        if (!TryGetSelectedSessionForTranscriptRefresh(out var sessionId, out _))
        {
            StopSelectedSessionTranscriptRefresh();
            return;
        }

        if (_selectedSessionTranscriptRefreshTimer != null &&
            string.Equals(_selectedSessionTranscriptRefreshSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        StopSelectedSessionTranscriptRefresh();
        _selectedSessionTranscriptRefreshSessionId = sessionId;
        _selectedSessionTranscriptRefreshTimer = new System.Threading.Timer(_ =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                    return;

                if (!TryGetSelectedSessionForTranscriptRefresh(out var _, out SessionSnapshot _))
                {
                    StopSelectedSessionTranscriptRefresh();
                    return;
                }

                RefreshSelectedSessionTranscriptForCurrentDetail();
            });
        }, null, SelectedSessionTranscriptRefreshInterval, SelectedSessionTranscriptRefreshInterval);
    }

    private void StopSelectedSessionTranscriptRefresh()
    {
        _selectedSessionTranscriptRefreshTimer?.Dispose();
        _selectedSessionTranscriptRefreshTimer = null;
        _selectedSessionTranscriptRefreshSessionId = null;
    }

    private static WpfHudListItemKind GetHudSessionListItemKind(SessionSnapshot? session) =>
        session?.Status == AgentStatus.Completed ||
        (session?.Status == AgentStatus.Idle && (!string.IsNullOrWhiteSpace(session.CompletionText) || !string.IsNullOrWhiteSpace(session.LastAssistantMessage)))
            ? WpfHudListItemKind.Completed
            : WpfHudListItemKind.Running;

    private static bool IsSafeReadOnlyPermissionRequest(PermissionRequest request) =>
        request.ToolName is "Read" or "Grep" or "Glob" or "LS" or "TodoRead";

    private void RemoveCompletion(TaskCompletionSource<string> completion)
    {
        var originalPermissionCount = _permissionQueue.Count;
        var originalQuestionCount = _questionQueue.Count;
        var permissions = _permissionQueue.Where(p => p.Completion != completion).ToArray();
        _permissionQueue.Clear();
        foreach (var item in permissions) _permissionQueue.Enqueue(item);
        var questions = _questionQueue.Where(q => q.Completion != completion).ToArray();
        _questionQueue.Clear();
        foreach (var item in questions) _questionQueue.Enqueue(item);
        if (permissions.Length != originalPermissionCount || questions.Length != originalQuestionCount)
            _pendingActionRevision++;
    }

    private void RefreshAll()
    {
        if (IsHudVisualUpdateDeferred)
        {
            _hudVisualRefreshPending = true;
            return;
        }

        RefreshAllCore();
    }

    private void RefreshAllCore()
    {
        RebuildHudListItems();
        OnPropertyChanged(string.Empty);
        OnPropertyChanged(nameof(ShouldShowPendingAlert));
        OnPropertyChanged(nameof(DetailUserPrompt));
        OnPropertyChanged(nameof(DetailAssistantReplyTitle));
        OnPropertyChanged(nameof(DetailAssistantReply));
        OnPropertyChanged(nameof(DetailToolText));
        OnPropertyChanged(nameof(HasHudListItems));
        OnPropertyChanged(nameof(HasNoHudListItems));
        OnPropertyChanged(nameof(HasExpandedHudListSessionDetail));
        UpdateSelectedSessionTranscriptRefresh();
    }

    private bool IsHudVisualUpdateDeferred => _hudVisualUpdateDeferralDepth > 0;

    private void FlushDeferredHudVisualUpdates()
    {
        var shouldSyncSessions = _hudSessionItemsRefreshPending;
        var shouldRefreshQuestions = _hudQuestionOptionsRefreshPending;
        var shouldRefreshAll = _hudVisualRefreshPending;

        _hudSessionItemsRefreshPending = false;
        _hudQuestionOptionsRefreshPending = false;
        _hudVisualRefreshPending = false;

        if (shouldSyncSessions)
            SyncDeferredSessionItems();

        if (shouldRefreshQuestions)
            RefreshQuestionOptionsCore();

        if (shouldRefreshAll || shouldSyncSessions)
            RefreshAllCore();
    }

    private string FormatRecentMessage(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        if (_settings.Get("show_full_recent_messages", false))
            return text;

        var firstLine = text.Replace("\r", " ", StringComparison.Ordinal).Split('\n', 2)[0].Trim();
        return firstLine.Length <= 180 ? firstLine : firstLine[..180] + "…";
    }

    private static string BuildPermissionContent(PermissionRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Description))
            parts.Add(request.Description!);

        if (!string.IsNullOrWhiteSpace(request.ToolName))
            parts.Add($"工具: {request.ToolName}");

        var input = FormatToolInput(request.ToolInput);
        if (!string.IsNullOrWhiteSpace(input) && input != "等待用户确认")
            parts.Add(input);

        return parts.Count == 0 ? "等待用户确认" : string.Join(Environment.NewLine, parts);
    }

    private static string FormatToolInput(Dictionary<string, object?>? input)
    {
        if (input == null || input.Count == 0)
            return "等待用户确认";

        static string FormatValue(object? value) => value switch
        {
            null => "null",
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText(),
            _ => value.ToString() ?? ""
        };

        string[] priorityKeys = ["justification", "command", "prefix_rule", "prefixRule", "sandbox_permissions", "sandboxPermissions"];
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<KeyValuePair<string, object?>>();

        foreach (var key in priorityKeys)
        {
            var match = input.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Key) && emitted.Add(match.Key))
                ordered.Add(match);
        }

        foreach (var pair in input)
        {
            if (emitted.Add(pair.Key))
                ordered.Add(pair);
        }

        return string.Join(Environment.NewLine, ordered.Select(p => $"{p.Key}: {FormatValue(p.Value)}"));
    }

    private static string BuildPermissionAllowResponse(HookEvent evt, PermissionRequest request, bool always) =>
        HookResponseBuilder.BuildPermissionAllowResponse(evt, request, always);

    private static string BuildPermissionDenyResponse(HookEvent evt, string reason) =>
        HookResponseBuilder.BuildPermissionDenyResponse(evt, reason);

    private static string BuildQuestionAnswerResponse(PendingQuestion pending) =>
        HookResponseBuilder.BuildQuestionAnswerResponse(pending.Event, pending.Question, pending.Answers);

    private static string BuildQuestionDismissResponse(HookEvent evt, string reason) =>
        HookResponseBuilder.BuildQuestionDismissResponse(evt, reason);

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key is "display_position" or "hud_density_mode")
        {
            NotifyHudLayoutProperties();
        }
        else if (e.Key == "show_full_recent_messages")
        {
            if (IsHudVisualUpdateDeferred)
            {
                foreach (var sessionId in _sessionItems.Keys)
                    _deferredSessionItemIds.Add(sessionId);

                _hudSessionItemsRefreshPending = true;
                _hudVisualRefreshPending = true;
                return;
            }

            foreach (var item in _sessionItems.Values)
                item.RefreshDisplay();
            RefreshAll();
        }
    }

    private void NotifyHudLayoutProperties()
    {
        OnPropertyChanged(nameof(IsSideCollapsed));
        OnPropertyChanged(nameof(IsHorizontalCollapsed));
        OnPropertyChanged(nameof(IsCompactHudMode));
        OnPropertyChanged(nameof(IsClassicHudMode));
        OnPropertyChanged(nameof(IsClassicHorizontalCollapsed));
        OnPropertyChanged(nameof(IsCompactHorizontalCollapsed));
        OnPropertyChanged(nameof(IsClassicSideCollapsed));
        OnPropertyChanged(nameof(IsCompactSideCollapsed));
        OnPropertyChanged(nameof(ShouldShowPendingAlert));
    }

    public void Dispose()
    {
        _settings.SettingChanged -= OnSettingChanged;
        _disposed = true;
        _completionTimer?.Dispose();
        _processMonitorTimer?.Dispose();
        StopSelectedSessionTranscriptRefresh();
    }

    public event Action<string>? PlaySoundRequested;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record PendingPermission(string ActionId, DateTime CreatedAt, PermissionRequest Request, TaskCompletionSource<string> Completion, HookEvent Event);

    private sealed class PendingQuestion
    {
        public PendingQuestion(string actionId, DateTime createdAt, QuestionData question, TaskCompletionSource<string> completion, HookEvent @event)
        {
            ActionId = actionId;
            CreatedAt = createdAt;
            Question = question;
            Completion = completion;
            Event = @event;
        }

        public string ActionId { get; }
        public DateTime CreatedAt { get; }
        public QuestionData Question { get; }
        public TaskCompletionSource<string> Completion { get; }
        public HookEvent Event { get; }
        public int CurrentQuestionIndex { get; set; }
        public Dictionary<string, IReadOnlyList<string>> Answers { get; } = new(StringComparer.Ordinal);
        public QuestionItem? CurrentItem => Question.Questions is { Count: > 0 } questions
            ? questions[Math.Clamp(CurrentQuestionIndex, 0, questions.Count - 1)]
            : null;
        public string CurrentQuestionText => CurrentItem?.Question ?? Question.Question;
        public string CurrentAnswerKey => CurrentItem?.Id ?? Question.Id ?? CurrentQuestionText;
        public bool CurrentMultiSelect => CurrentItem?.MultiSelect ?? Question.MultiSelect;
        public IReadOnlyList<QuestionOption> CurrentOptions => CurrentItem?.Options ?? Question.Options ?? [];
        public void ClearAnswerState() => Answers.Clear();
    }
}

public sealed class WpfQuestionOptionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public WpfQuestionOptionViewModel(QuestionOption option)
    {
        Label = option.Label;
        Description = option.Description ?? "";
        ResponseValue = string.IsNullOrWhiteSpace(option.Value) ? option.Label : option.Value!;
    }

    public string Label { get; }
    public string Description { get; }
    public string ResponseValue { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
