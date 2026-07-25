using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using CodeIsland.Contracts;
using CodeIsland.WpfApp.Models;
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
    private const string NeutralHudSource = "codeisland";

    private readonly SettingsManager _settings;
    private readonly WpfWebhookNotifier? _webhookNotifier;
    private IWpfRuntimeClient _runtimeClient;
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WpfSessionItemViewModel> _sessionItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedHudSessionIds = new(StringComparer.Ordinal);
    private readonly Queue<PendingPermission> _permissionQueue = new();
    private readonly Queue<PendingQuestion> _questionQueue = new();
    private readonly ConcurrentDictionary<string, byte> _autoApprovingPermissionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notifiedWebhookActionKeys = new(StringComparer.Ordinal);
    /// <summary>
    /// 展示层本地失效的 pending actionId。仅影响 HUD 投影，不代表已向服务端提交 allow/deny/answer。
    /// 当服务端 pending 列表不再包含该 actionId 时清理。
    /// </summary>
    private readonly HashSet<string> _locallyInvalidatedPendingActionIds = new(StringComparer.Ordinal);
    private WpfHudSurfaceKind _surfaceKind = WpfHudSurfaceKind.Collapsed;
    private string? _selectedSessionId;
    private string? _selectedHudItemId;
    private string? _selectedPendingActionId;
    private WpfHudListItemKind? _selectedPendingActionKind;
    private string? _completionSessionId;
    private int _pendingActionRevision;
    private System.Threading.Timer? _completionTimer;
    private System.Threading.Timer? _selectedSessionTranscriptRefreshTimer;
    private string? _selectedSessionTranscriptRefreshSessionId;
    private bool _disposed;
    private readonly HashSet<string> _deferredSessionItemIds = new(StringComparer.Ordinal);
    private int _hudVisualUpdateDeferralDepth;
    private bool _hudVisualRefreshPending;
    private bool _hudQuestionOptionsRefreshPending;
    private bool _hudSessionItemsRefreshPending;
    private bool _isPendingPinned;

    public WpfAppState(SettingsManager settings, IWpfRuntimeClient runtimeClient, WpfWebhookNotifier? webhookNotifier = null)
    {
        _settings = settings;
        _webhookNotifier = webhookNotifier;
        _runtimeClient = runtimeClient;
        ShowSessionListCommand = new RelayCommand(ShowSessionList);
        CollapseCommand = new RelayCommand(Collapse);
        DismissCompletionCommand = new RelayCommand(DismissCompletion);
        ApproveCommand = new RelayCommand(() => Approve(false));
        AlwaysApproveCommand = new RelayCommand(() => Approve(true));
        DenyCommand = new RelayCommand(Deny);
        DismissPermissionCommand = new RelayCommand(DismissPermission);
        DismissQuestionCommand = new RelayCommand(DismissQuestion);
        TogglePendingPinCommand = new RelayCommand(TogglePendingPin);
        SubmitQuestionCommand = new RelayCommand(_ => SubmitQuestion(QuestionAnswer));
        SelectQuestionOptionCommand = new RelayCommand(parameter => HandleQuestionOption(parameter as WpfQuestionOptionViewModel));
        OpenSessionCommand = new RelayCommand(parameter => OpenSession(parameter as string));
        OpenHudListItemCommand = new RelayCommand(parameter => OpenHudListItem(parameter as string));
        RemoveHudListItemCommand = new RelayCommand(parameter => RemoveHudListItem(parameter as string));
        JumpToTerminalCommand = new RelayCommand(parameter => JumpToTerminal(parameter as string));
        SelectedSessionJumpToTerminalCommand = new RelayCommand(() => JumpToTerminal(_selectedSessionId));
        _settings.SettingChanged += OnSettingChanged;
        _runtimeClient.StateChanged += OnHubStateChanged;
    }

    /// <summary>
    /// 替换 CodeOrbit API 客户端（应用内重连）。退订旧客户端、清空会话/待办投影并订阅新客户端。
    /// 不负责 Dispose 旧客户端（由 App 编排）。
    /// </summary>
    public void ReplaceClient(IWpfRuntimeClient runtimeClient)
    {
        ArgumentNullException.ThrowIfNull(runtimeClient);

        void Apply()
        {
            if (_disposed)
                return;

            if (ReferenceEquals(_runtimeClient, runtimeClient))
                return;

            _runtimeClient.StateChanged -= OnHubStateChanged;
            _runtimeClient = runtimeClient;
            _runtimeClient.StateChanged += OnHubStateChanged;
            ClearRuntimeProjection();
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(Apply);
        else
            Apply();
    }

    private void ClearRuntimeProjection()
    {
        foreach (var sessionId in _sessions.Keys.ToArray())
            RemoveHubSessionProjection(sessionId);

        _permissionQueue.Clear();
        _questionQueue.Clear();
        _autoApprovingPermissionIds.Clear();
        _locallyInvalidatedPendingActionIds.Clear();
        _notifiedWebhookActionKeys.Clear();
        _removedHudSessionIds.Clear();
        _selectedSessionId = null;
        _selectedHudItemId = null;
        _selectedPendingActionId = null;
        _selectedPendingActionKind = null;
        IsPendingPinned = false;
        _pendingActionRevision++;
        QuestionAnswer = "";

        // 会话清空后务必停掉定时器，避免重连窗口期回调读到旧投影
        _completionTimer?.Dispose();
        _completionTimer = null;
        _completionSessionId = null;
        StopSelectedSessionTranscriptRefresh();

        RefreshQuestionOptions();
        if (SurfaceKind != WpfHudSurfaceKind.Collapsed)
            SurfaceKind = WpfHudSurfaceKind.Collapsed;
        RefreshAll();
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
    public bool IsPendingPinned
    {
        get => _isPendingPinned;
        private set
        {
            if (_isPendingPinned == value)
                return;
            _isPendingPinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PendingPinButtonText));
        }
    }
    public string PendingPinButtonText => IsPendingPinned ? "取消钉住" : "钉住";
    public bool IsSideCollapsed => IsCollapsed && !IsOrbHudMode && WpfHudDisplayPosition.IsSideCenter(_settings.Get("display_position", WpfHudDisplayPosition.Default));
    public bool IsHorizontalCollapsed => IsCollapsed && !IsSideCollapsed && !IsOrbHudMode;
    public bool IsCompactHudMode => WpfHudDensityMode.IsCompact(_settings.Get("hud_density_mode", WpfHudDensityMode.Default));
    public bool IsOrbHudMode => WpfHudDensityMode.IsOrb(_settings.Get("hud_density_mode", WpfHudDensityMode.Default));
    public bool IsClassicHudMode => !IsCompactHudMode && !IsOrbHudMode;
    public bool IsClassicHorizontalCollapsed => IsHorizontalCollapsed && IsClassicHudMode;
    public bool IsCompactHorizontalCollapsed => IsHorizontalCollapsed && IsCompactHudMode;
    public bool IsClassicSideCollapsed => IsSideCollapsed && IsClassicHudMode;
    public bool IsCompactSideCollapsed => IsSideCollapsed && IsCompactHudMode;
    public bool IsOrbCollapsed => IsCollapsed && IsOrbHudMode;
    public bool ShouldShowPendingAlert => HasPendingAction;
    public string? SelectedHudItemId => _selectedHudItemId;

    public string PermissionTitle => GetCurrentPermission()?.Request is { } p ? $"{p.ToolName} 请求权限" : "权限审批";
    public string PermissionDescription => GetCurrentPermission()?.Request is { } p ? BuildPermissionContent(p) : "";
    public string PermissionCommand => GetCurrentPermission()?.Request is { } p ? BuildPermissionContent(p) : "等待用户确认";
    public string PermissionProject => GetCurrentPermission() is { } p && _sessions.TryGetValue(p.Request.SessionId, out var s) ? s.ProjectName ?? s.WorkingDirectory ?? "未知项目" : "未知项目";
    public string PermissionSource => GetCurrentPermission() is { } p && _sessions.TryGetValue(p.Request.SessionId, out var s) ? WpfSourceDisplay.GetDisplayName(s.Source, s.SourceDisplayName) : "未知工具";
    public string PermissionWorkDir => GetCurrentPermission() is { } p && _sessions.TryGetValue(p.Request.SessionId, out var s) ? s.WorkingDirectory ?? s.ProjectName ?? "未知路径" : "未知路径";
    public string QuestionTitle => GetCurrentQuestion() is { } q ? q.CurrentItem?.Header ?? q.Question.Header ?? "需要你的回答" : "需要你的回答";
    public string QuestionSource => GetCurrentQuestion() is { } q && _sessions.TryGetValue(q.Question.SessionId, out var s) ? WpfSourceDisplay.GetDisplayName(s.Source, s.SourceDisplayName) : "未知工具";
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
    // 详情/完成卡要完整 Markdown 原文，不再截首行
    public string DetailAssistantReply => FormatFullMessage(GetSelectedSessionAssistantReply(), $"暂无 {SelectedSession?.Source ?? "AI"} 回复");
    public string CompletionTitle => CompletionSession == null ? "回复已完成" : $"{WpfSourceDisplay.GetDisplayName(CompletionSession.Source, CompletionSession.SourceDisplayName)} 回复已完成";
    public string CompletionSource => CompletionSession == null ? "未知工具" : WpfSourceDisplay.GetDisplayName(CompletionSession.Source, CompletionSession.SourceDisplayName);
    public string CompletionProject => CompletionSession?.ProjectName ?? CompletionSession?.WorkingDirectory ?? "未知项目";
    public string CompletionUserPrompt => FormatRecentMessage(CompletionSession?.LastUserPrompt, "");
    public bool HasCompletionUserPrompt => !string.IsNullOrWhiteSpace(CompletionUserPrompt);
    public string CompletionText => FormatFullMessage(CompletionSession?.CompletionText ?? CompletionSession?.LastAssistantMessage, "回复已完成");

    public ICommand ShowSessionListCommand { get; }
    public ICommand CollapseCommand { get; }
    public ICommand DismissCompletionCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand AlwaysApproveCommand { get; }
    public ICommand DenyCommand { get; }
    public ICommand DismissPermissionCommand { get; }
    public ICommand DismissQuestionCommand { get; }
    public ICommand TogglePendingPinCommand { get; }
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

    private void OnHubStateChanged(object? sender, WpfRuntimeStateChangedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyHubState(e));
            return;
        }

        ApplyHubState(e);
    }

    private void ApplyHubState(WpfRuntimeStateChangedEventArgs change)
    {
        var previousQuestionCursor = GetCurrentQuestionCursor();
        var previousPendingSignature = BuildPendingSignature();
        var incomingSessionIds = change.Sessions
            .Select(static session => session.SessionId)
            .Where(static sessionId => !string.IsNullOrWhiteSpace(sessionId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var removedSessionId in _sessions.Keys.Where(sessionId => !incomingSessionIds.Contains(sessionId)).ToArray())
            RemoveHubSessionProjection(removedSessionId);

        foreach (var snapshot in change.Sessions)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SessionId))
                continue;

            var clone = snapshot.Clone();
            _sessions[clone.SessionId] = clone;
            SyncSessionItem(clone.SessionId, clone);
        }

        if (!string.IsNullOrWhiteSpace(change.AffectedSessionId) &&
            _sessions.TryGetValue(change.AffectedSessionId, out var affectedSession) &&
            ShouldRestoreRemovedHudSession(change.NormalizedEventName ?? "", affectedSession.Status))
        {
            _removedHudSessionIds.Remove(change.AffectedSessionId);
        }

        ReplacePendingProjection(change.PendingActions);
        PruneNotifiedWebhookKeys();
        var currentQuestionCursor = GetCurrentQuestionCursor();
        if (!string.Equals(previousQuestionCursor.ActionId, currentQuestionCursor.ActionId, StringComparison.Ordinal) ||
            !string.Equals(previousQuestionCursor.AnswerKey, currentQuestionCursor.AnswerKey, StringComparison.Ordinal))
        {
            QuestionAnswer = "";
        }

        var pendingProjectionChanged =
            !string.Equals(previousPendingSignature, BuildPendingSignature(), StringComparison.Ordinal);
        if (pendingProjectionChanged)
            _pendingActionRevision++;

        // 外部处理后本地失效/权威源清空：清理已失效的选中 pending，并尽量落到下一个有效项。
        if (_selectedPendingActionId != null && !HasPendingActionProjection(_selectedPendingActionId))
        {
            _selectedPendingActionId = null;
            _selectedPendingActionKind = null;
            if (_selectedHudItemId?.StartsWith("permission:", StringComparison.Ordinal) == true ||
                _selectedHudItemId?.StartsWith("question:", StringComparison.Ordinal) == true)
            {
                _selectedHudItemId = null;
            }

            if (SurfaceKind == WpfHudSurfaceKind.HudDetail)
            {
                // 先重建列表，才能定位下一个有效 pending（与用户主动审批后的 Advance 路径对齐）。
                RebuildHudListItems();
                var nextPending = HudListItems.FirstOrDefault(static item =>
                    item.Kind is WpfHudListItemKind.Permission or WpfHudListItemKind.Question);
                if (nextPending != null)
                {
                    _selectedHudItemId = nextPending.ItemId;
                    _selectedSessionId = nextPending.SessionId;
                    _selectedPendingActionId = nextPending.ItemId.Split(':').LastOrDefault();
                    _selectedPendingActionKind = nextPending.Kind;
                    SurfaceKind = WpfHudSurfaceKind.HudDetail;
                    QuestionAnswer = "";
                }
                else
                {
                    SurfaceKind = WpfHudSurfaceKind.SessionList;
                }
            }
        }
        else if (!HasPendingAction &&
                 SurfaceKind == WpfHudSurfaceKind.HudDetail &&
                 _selectedPendingActionId == null &&
                 (_selectedHudItemId == null ||
                  _selectedHudItemId.StartsWith("permission:", StringComparison.Ordinal) ||
                  _selectedHudItemId.StartsWith("question:", StringComparison.Ordinal)))
        {
            // 兜底：已无 pending 但仍停在审批/问答详情时，退回列表。
            SurfaceKind = WpfHudSurfaceKind.SessionList;
        }

        if (_selectedSessionId == null &&
            !string.IsNullOrWhiteSpace(change.AffectedSessionId) &&
            _sessions.ContainsKey(change.AffectedSessionId))
        {
            _selectedSessionId = change.AffectedSessionId;
        }
        _selectedSessionId ??= _sessions.Keys.FirstOrDefault();

        if (HasPendingAction)
            ClearCompletionCardForPendingAction();

        if (!string.IsNullOrWhiteSpace(change.AffectedSessionId) &&
            _sessions.TryGetValue(change.AffectedSessionId, out var changedSession))
        {
            // Only notify status changes for the affected session (not every pending-only event).
            if (change.Effect is SideEffect.None or SideEffect.PlaySound)
                _webhookNotifier?.NotifySessionChanged(changedSession);
        }

        switch (change.Effect)
        {
            case SideEffect.ShowApprovalCard approval:
                NotifyWebhookApprovalOnce(change.AffectedActionId, approval.Request);
                break;
            case SideEffect.ShowQuestionCard question:
                NotifyWebhookQuestionOnce(change.AffectedActionId, question.Question);
                QuestionAnswer = "";
                RefreshQuestionOptions();
                break;
            case SideEffect.PlaySound ps when !string.IsNullOrWhiteSpace(change.AffectedSessionId) && IsVisibleHudSession(change.AffectedSessionId):
                PlaySoundRequested?.Invoke(ps.SoundName);
                if (ps.SoundName == "complete")
                    ShowCompletion(change.AffectedSessionId);
                break;
        }

        // pending 投影变化（含本地失效）时必须走完整 RefreshAll，避免 in-place early-return 漏刷新悬浮层/角标。
        if (!pendingProjectionChanged &&
            change.Effect is SideEffect.None &&
            !string.IsNullOrWhiteSpace(change.AffectedSessionId) &&
            _sessions.TryGetValue(change.AffectedSessionId, out var session) &&
            TryUpdateExpandedInlineSessionItemInPlace(change.AffectedSessionId, session))
        {
            UpdateSelectedSessionTranscriptRefresh();
            return;
        }

        RefreshQuestionOptions();
        RefreshAll();
    }

    private void RemoveHubSessionProjection(string sessionId)
    {
        _sessions.Remove(sessionId);
        _removedHudSessionIds.Remove(sessionId);

        if (_sessionItems.ContainsKey(sessionId))
        {
            if (IsHudVisualUpdateDeferred)
            {
                _deferredSessionItemIds.Add(sessionId);
                _hudSessionItemsRefreshPending = true;
            }
            else
            {
                RemoveSessionItemCore(sessionId);
            }
        }

        if (string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal))
        {
            _selectedSessionId = null;
            if (_selectedHudItemId?.Equals($"session:{sessionId}", StringComparison.Ordinal) == true)
                _selectedHudItemId = null;
        }

        if (string.Equals(_completionSessionId, sessionId, StringComparison.Ordinal))
        {
            _completionTimer?.Dispose();
            _completionTimer = null;
            _completionSessionId = null;
            if (SurfaceKind == WpfHudSurfaceKind.CompletionCard)
                SurfaceKind = WpfHudSurfaceKind.Collapsed;
        }
    }

    private void ReplacePendingProjection(IReadOnlyList<WpfPendingActionSnapshot> pendingActions)
    {
        _permissionQueue.Clear();
        _questionQueue.Clear();
        var autoApproveAllPermissions = _settings.Get(SettingsManager.AutoApproveAllPermissionsKey, false);

        var serverActionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in pendingActions)
        {
            if (!string.IsNullOrWhiteSpace(pending.ActionId))
                serverActionIds.Add(pending.ActionId);
        }

        // 服务端已移除的 action 不再需要本地失效记录。
        _locallyInvalidatedPendingActionIds.RemoveWhere(actionId => !serverActionIds.Contains(actionId));

        foreach (var pending in pendingActions.OrderBy(static action => action.CreatedAt))
        {
            if (string.IsNullOrWhiteSpace(pending.ActionId))
                continue;

            // 权威源仍返回 pending 时，用会话推进信号做展示层失效兜底（不提交 allow/deny/answer）。
            if (_locallyInvalidatedPendingActionIds.Contains(pending.ActionId) ||
                IsStalePendingAction(pending))
            {
                _locallyInvalidatedPendingActionIds.Add(pending.ActionId);
                continue;
            }

            if (pending.Permission != null)
            {
                if (autoApproveAllPermissions)
                {
                    QueueAutoApprovePermission(pending.ActionId);
                    continue;
                }

                _permissionQueue.Enqueue(new PendingPermission(pending.ActionId, pending.CreatedAt, pending.Permission));
            }
            else if (pending.Question != null)
            {
                _questionQueue.Enqueue(new PendingQuestion(
                    pending.ActionId,
                    pending.CreatedAt,
                    pending.Question,
                    pending.CurrentQuestionIndex));
            }
        }
    }

    /// <summary>
    /// 判断 pending 是否因会话已在别处处理后继续推进而过期。
    /// 多题问答的题号/CurrentAnswerKey 推进本身不算失效。
    /// </summary>
    private bool IsStalePendingAction(WpfPendingActionSnapshot pending)
    {
        if (string.IsNullOrWhiteSpace(pending.SessionId))
            return false;

        if (!_sessions.TryGetValue(pending.SessionId, out var session))
            return false;

        var isPermission = pending.Permission != null;
        var isQuestion = pending.Question != null;
        if (!isPermission && !isQuestion)
            return false;

        // 主信号：会话已离开对应等待态（权限/问答分别判断，避免误伤另一类真实等待）。
        if (isPermission && session.Status != AgentStatus.WaitingApproval)
            return true;

        if (isQuestion && session.Status != AgentStatus.WaitingQuestion)
            return true;

        // 仍处于等待态时，仅在出现明确推进信号时本地失效（不单独依赖 LastUpdated 心跳）。
        return HasStrongSessionProgressAfterPending(session, pending);
    }

    private static bool HasStrongSessionProgressAfterPending(
        SessionSnapshot session,
        WpfPendingActionSnapshot pending)
    {
        var createdAt = pending.CreatedAt;

        // 新消息：例如 CLI 在别处处理后继续输出/接收下一条消息。
        foreach (var message in session.RecentMessages)
        {
            if (message.Timestamp > createdAt)
                return true;
        }

        // 新工具历史：pending 创建后出现其它工具推进。忽略与当前权限请求同名的条目，避免把“正在等待的工具”误判为已继续。
        var waitingToolName = pending.Permission?.ToolName;
        foreach (var entry in session.ToolHistory)
        {
            if (entry.Timestamp <= createdAt)
                continue;

            if (!string.IsNullOrWhiteSpace(waitingToolName) &&
                string.Equals(entry.ToolName, waitingToolName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private string BuildPendingSignature()
    {
        var permissionKeys = _permissionQueue.Select(pending => $"p:{pending.ActionId}");
        var questionKeys = _questionQueue.Select(pending => $"q:{pending.ActionId}:{pending.CurrentQuestionIndex}:{pending.CurrentAnswerKey}");
        return string.Join("|", permissionKeys.Concat(questionKeys));
    }

    private bool HasPendingActionProjection(string actionId) =>
        _permissionQueue.Any(pending => pending.ActionId == actionId) ||
        _questionQueue.Any(pending => pending.ActionId == actionId);

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

        _ = _runtimeClient.ActivateTerminalAsync(session.SessionId);
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
        _completionTimer = new System.Threading.Timer(_ => System.Windows.Application.Current.Dispatcher.Invoke(DismissCompletion), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
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
        _completionTimer = new System.Threading.Timer(_ => System.Windows.Application.Current.Dispatcher.Invoke(DismissCompletion), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    public void Approve(bool always)
    {
        var pending = GetCurrentPermission();
        if (pending == null)
            return;

        _ = RunRuntimeCommandAsync(
            () => _runtimeClient.AllowPermissionAsync(pending.ActionId, always),
            AdvanceAfterPendingResponse);
    }

    private void ApproveAllPendingPermissionsIfEnabled()
    {
        if (!_settings.Get(SettingsManager.AutoApproveAllPermissionsKey, false))
            return;

        if (_permissionQueue.Count == 0)
            return;

        foreach (var pending in _permissionQueue.ToArray())
            QueueAutoApprovePermission(pending.ActionId);

        _permissionQueue.Clear();
        AdvanceAfterPendingResponse();
    }

    private void QueueAutoApprovePermission(string actionId)
    {
        if (_autoApprovingPermissionIds.TryAdd(actionId, 0))
            _ = AutoApprovePermissionAsync(actionId);
    }

    private async Task AutoApprovePermissionAsync(string actionId)
    {
        try
        {
            await _runtimeClient.AllowPermissionAsync(actionId, false);
        }
        catch
        {
        }
        finally
        {
            _autoApprovingPermissionIds.TryRemove(actionId, out _);
        }
    }

    public void Deny()
    {
        var pending = GetCurrentPermission();
        if (pending == null)
            return;

        _ = RunRuntimeCommandAsync(
            () => _runtimeClient.DenyPermissionAsync(pending.ActionId, "user denied"),
            AdvanceAfterPendingResponse);
    }

    public void DismissPermission()
    {
        var pending = GetCurrentPermission();
        if (pending == null)
            return;

        _ = RunRuntimeCommandAsync(
            () => _runtimeClient.DenyPermissionAsync(pending.ActionId, "dismissed"),
            AdvanceAfterPendingResponse);
    }

    public void DismissQuestion()
    {
        var pending = GetCurrentQuestion();
        if (pending == null)
            return;

        _ = RunRuntimeCommandAsync(
            () => _runtimeClient.DismissQuestionAsync(pending.ActionId, "dismissed"),
            AdvanceAfterPendingResponse);
    }

    public void SubmitQuestion(string answer)
    {
        var pending = GetCurrentQuestion();
        if (pending == null) return;
        var resolvedAnswers = pending.CurrentMultiSelect
            ? QuestionOptions.Where(static option => option.IsSelected).Select(static option => option.ResponseValue).ToArray()
            : [answer];

        _ = SubmitCurrentQuestionAsync(pending.ActionId, resolvedAnswers);
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

        _ = SubmitCurrentQuestionAsync(pending.ActionId, [option.ResponseValue]);
    }

    private async Task SubmitCurrentQuestionAsync(string actionId, IReadOnlyList<string> answers)
    {
        try
        {
            var result = await _runtimeClient.AnswerCurrentQuestionAsync(actionId, answers);
            if (!result.Success)
                return;

            QuestionAnswer = "";
            if (result.Resolved)
                AdvanceAfterPendingResponse();
        }
        catch
        {
        }
    }

    private static async Task RunRuntimeCommandAsync(Func<Task<bool>> command, Action onSuccess)
    {
        try
        {
            if (await command())
                onSuccess();
        }
        catch
        {
        }
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

    private (string? ActionId, string? AnswerKey) GetCurrentQuestionCursor() =>
        GetCurrentQuestion() is { } pending ? (pending.ActionId, pending.CurrentAnswerKey) : (null, null);

    private void AdvanceAfterPendingResponse()
    {
        _selectedPendingActionId = null;
        _selectedPendingActionKind = null;
        if (!HasPendingAction)
            IsPendingPinned = false;
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

    public void TogglePendingPin()
    {
        if (!HasPendingAction)
        {
            IsPendingPinned = false;
            return;
        }

        IsPendingPinned = !IsPendingPinned;
    }

    private static bool HasCompletionContent(SessionSnapshot session) =>
        !string.IsNullOrWhiteSpace(session.CompletionText) ||
        !string.IsNullOrWhiteSpace(session.LastAssistantMessage);

    private void RebuildHudListItems()
    {
        var pendingSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desired = new List<WpfHudListItemViewModel>();
        var existingById = HudListItems.ToDictionary(static item => item.ItemId, StringComparer.Ordinal);

        foreach (var pending in _permissionQueue)
        {
            var session = GetSession(pending.Request.SessionId);
            if (!string.IsNullOrWhiteSpace(pending.Request.SessionId))
                pendingSessionIds.Add(pending.Request.SessionId);

            var itemId = $"permission:{pending.ActionId}";
            if (existingById.TryGetValue(itemId, out var existing) && existing.Kind == WpfHudListItemKind.Permission)
            {
                existing.UpdateSessionPresentation(
                    "权限请求",
                    GetSessionProjectName(session),
                    BuildPermissionSummary(pending.Request),
                    GetSessionSourceKey(session),
                    GetSessionSourceDisplayName(session),
                    "等待审批",
                    AgentStatus.WaitingApproval,
                    "#FFFFB86B",
                    FormatAge(pending.CreatedAt),
                    "",
                    "");
                desired.Add(existing);
            }
            else
            {
                desired.Add(new WpfHudListItemViewModel(
                    itemId,
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
        }

        foreach (var pending in _questionQueue)
        {
            var session = GetSession(pending.Question.SessionId);
            if (!string.IsNullOrWhiteSpace(pending.Question.SessionId))
                pendingSessionIds.Add(pending.Question.SessionId);

            var itemId = $"question:{pending.ActionId}";
            if (existingById.TryGetValue(itemId, out var existing) && existing.Kind == WpfHudListItemKind.Question)
            {
                existing.UpdateSessionPresentation(
                    "问答请求",
                    GetSessionProjectName(session),
                    pending.CurrentQuestionText,
                    GetSessionSourceKey(session),
                    GetSessionSourceDisplayName(session),
                    "等待回答",
                    AgentStatus.WaitingQuestion,
                    "#FF7AB8FF",
                    FormatAge(pending.CreatedAt),
                    "",
                    "");
                desired.Add(existing);
            }
            else
            {
                desired.Add(new WpfHudListItemViewModel(
                    itemId,
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
        }

        foreach (var vm in _sessionItems.Values)
        {
            if (pendingSessionIds.Contains(vm.SessionId))
                continue;
            if (_removedHudSessionIds.Contains(vm.SessionId))
                continue;

            var session = GetSession(vm.SessionId);
            var kind = GetHudSessionListItemKind(session);
            var itemId = $"session:{vm.SessionId}";
            var title = kind == WpfHudListItemKind.Completed ? "已完成" : "运行中";
            var accent = kind == WpfHudListItemKind.Completed ? "#FF8EE6D0" : "#FF7AB8FF";
            var detailUser = FormatSessionUserPrompt(session);
            var detailAssistant = FormatSessionAssistantReply(session);
            var shouldExpand = SurfaceKind == WpfHudSurfaceKind.SessionList &&
                string.Equals(_selectedHudItemId, itemId, StringComparison.Ordinal);

            if (existingById.TryGetValue(itemId, out var existing) &&
                existing.Kind is WpfHudListItemKind.Running or WpfHudListItemKind.Completed &&
                // Kind is immutable; recreate when Running <-> Completed flips.
                existing.Kind == kind)
            {
                existing.UpdateSessionPresentation(
                    title,
                    vm.Title,
                    vm.LastMessage,
                    vm.SourceKey,
                    vm.Source,
                    vm.StatusText,
                    vm.Status,
                    accent,
                    vm.TimeText,
                    detailUser,
                    detailAssistant);
                existing.IsExpanded = shouldExpand;
                desired.Add(existing);
            }
            else
            {
                desired.Add(new WpfHudListItemViewModel(
                    itemId,
                    kind,
                    vm.SessionId,
                    title,
                    vm.Title,
                    vm.LastMessage,
                    vm.SourceKey,
                    vm.Source,
                    vm.StatusText,
                    vm.Status,
                    accent,
                    vm.TimeText,
                    OpenHudListItemCommand,
                    detailUser,
                    detailAssistant,
                    shouldExpand));
            }
        }

        var sortedItems = desired
            .OrderBy(static item => item.Kind switch
            {
                WpfHudListItemKind.Permission => 0,
                WpfHudListItemKind.Question => 1,
                WpfHudListItemKind.Running => 2,
                _ => 3
            })
            .ThenBy(static item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        SyncObservableCollection(HudListItems, sortedItems);
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

        var existingGroups = HudListGroups.ToDictionary(static group => group.SourceKey, StringComparer.OrdinalIgnoreCase);
        var nextGroups = new List<WpfHudListGroupViewModel>(sourceOrder.Count);
        foreach (var sourceKey in sourceOrder)
        {
            if (!existingGroups.TryGetValue(sourceKey, out var group))
                group = new WpfHudListGroupViewModel(sourceKey);

            group.SyncItems(groupItems[sourceKey]);
            nextGroups.Add(group);
        }

        SyncObservableCollection(HudListGroups, nextGroups);
    }

    private static void SyncObservableCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count)
            {
                if (!ReferenceEquals(target[i], desired[i]))
                    target[i] = desired[i];
            }
            else
            {
                target.Add(desired[i]);
            }
        }

        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    private void NotifyWebhookApprovalOnce(string? actionId, PermissionRequest request)
    {
        if (_webhookNotifier is null)
            return;

        var key = string.IsNullOrWhiteSpace(actionId)
            ? $"permission:{request.SessionId}:{request.ToolUseId}:{request.ToolName}"
            : $"permission:{actionId}";
        if (!_notifiedWebhookActionKeys.Add(key))
            return;

        _webhookNotifier.NotifyApproval(request);
    }

    private void NotifyWebhookQuestionOnce(string? actionId, QuestionData question)
    {
        if (_webhookNotifier is null)
            return;

        var key = string.IsNullOrWhiteSpace(actionId)
            ? $"question:{question.SessionId}:{question.Question}"
            : $"question:{actionId}";
        if (!_notifiedWebhookActionKeys.Add(key))
            return;

        _webhookNotifier.NotifyQuestion(question);
    }

    private void PruneNotifiedWebhookKeys()
    {
        if (_notifiedWebhookActionKeys.Count == 0)
            return;

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in _permissionQueue)
            live.Add($"permission:{pending.ActionId}");
        foreach (var pending in _questionQueue)
            live.Add($"question:{pending.ActionId}");

        _notifiedWebhookActionKeys.RemoveWhere(key =>
            (key.StartsWith("permission:", StringComparison.Ordinal) ||
             key.StartsWith("question:", StringComparison.Ordinal)) &&
            !live.Contains(key));
    }

    private SessionSnapshot? GetSession(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out var session) ? session : null;

    private static string GetSessionProjectName(SessionSnapshot? session) =>
        session?.ProjectName ?? session?.WorkingDirectory ?? "未知项目";

    private static string GetSessionSourceDisplayName(SessionSnapshot? session) =>
        session != null ? WpfSourceDisplay.GetDisplayName(session.Source, session.SourceDisplayName) : "未知工具";

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
        var source = session == null ? "AI" : WpfSourceDisplay.GetDisplayName(session.Source, session.SourceDisplayName);
        // 内联展开详情同样渲染完整 Markdown
        return FormatFullMessage(GetSessionAssistantReply(session), $"暂无 {source} 回复");
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
        // Kind is immutable on the VM; force full rebuild when Running <-> Completed flips.
        if (selectedItem.Kind != kind)
            return false;

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
        return snapshot;
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
        if (!HasPendingAction)
            IsPendingPinned = false;

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

    private static string FormatFullMessage(string? text, string fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text;

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
        else if (e.Key == SettingsManager.AutoApproveAllPermissionsKey)
        {
            ApproveAllPendingPermissionsIfEnabled();
        }
    }

    private void NotifyHudLayoutProperties()
    {
        OnPropertyChanged(nameof(IsSideCollapsed));
        OnPropertyChanged(nameof(IsHorizontalCollapsed));
        OnPropertyChanged(nameof(IsCompactHudMode));
        OnPropertyChanged(nameof(IsOrbHudMode));
        OnPropertyChanged(nameof(IsClassicHudMode));
        OnPropertyChanged(nameof(IsClassicHorizontalCollapsed));
        OnPropertyChanged(nameof(IsCompactHorizontalCollapsed));
        OnPropertyChanged(nameof(IsClassicSideCollapsed));
        OnPropertyChanged(nameof(IsCompactSideCollapsed));
        OnPropertyChanged(nameof(IsOrbCollapsed));
        OnPropertyChanged(nameof(ShouldShowPendingAlert));
    }

    public void Dispose()
    {
        _settings.SettingChanged -= OnSettingChanged;
        _runtimeClient.StateChanged -= OnHubStateChanged;
        _disposed = true;
        _completionTimer?.Dispose();
        StopSelectedSessionTranscriptRefresh();
    }

    public event Action<string>? PlaySoundRequested;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static T InvokeOnDispatcher<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher == null || dispatcher.CheckAccess()
            ? action()
            : dispatcher.Invoke(action);
    }

    private sealed record PendingPermission(string ActionId, DateTime CreatedAt, PermissionRequest Request);

    private sealed class PendingQuestion
    {
        public PendingQuestion(
            string actionId,
            DateTime createdAt,
            QuestionData question,
            int currentQuestionIndex = 0)
        {
            ActionId = actionId;
            CreatedAt = createdAt;
            Question = question;
            CurrentQuestionIndex = currentQuestionIndex;
        }

        public string ActionId { get; }
        public DateTime CreatedAt { get; }
        public QuestionData Question { get; }
        public int CurrentQuestionIndex { get; }
        public QuestionItem? CurrentItem => Question.Questions is { Count: > 0 } questions
            ? questions[Math.Clamp(CurrentQuestionIndex, 0, questions.Count - 1)]
            : null;
        public string CurrentQuestionText => CurrentItem?.Question ?? Question.Question;
        public string CurrentAnswerKey => CurrentItem?.Id ?? Question.Id ?? CurrentQuestionText;
        public bool CurrentMultiSelect => CurrentItem?.MultiSelect ?? Question.MultiSelect;
        public IReadOnlyList<QuestionOption> CurrentOptions => CurrentItem?.Options ?? Question.Options ?? [];
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
