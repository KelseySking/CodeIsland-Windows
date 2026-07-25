using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodeIsland.WpfApp.Services;
using CodeIsland.WpfApp.ViewModels;

namespace CodeIsland.WpfApp.Views;

public partial class HudWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const double ShellPadding = 20d;
    private const double CompactCollapsedShellPadding = 8d;
    private const double PendingLayerGap = 8d;
    private static readonly Thickness PendingHostStackedMargin = new(0d, 0d, 0d, PendingLayerGap);
    private static readonly Thickness PendingHostOnlyMargin = new(0d);
    private const double CollapsedHorizontalWidth = 380d;
    private const double CollapsedHorizontalHeight = 64d;
    private const double CollapsedSideWidth = 64d;
    private const double CollapsedSideHeight = 162d;
    private const double HudContentWidth = 540d;
    private const double CompactCollapsedHorizontalWidth = 288d;
    private const double CompactCollapsedHorizontalHeight = 36d;
    private const double CompactCollapsedSideWidth = 42d;
    private const double CompactCollapsedSideHeight = 114d;
    private const double CompactHudContentWidth = 480d;
    private const double OrbCollapsedSize = 48d;
    private const double OrbShellPadding = 0d;
    private const double OrbCornerRadius = 12d;
    private const double OrbDragThreshold = 5d;
    // Open snappy + symmetric across densities; close deliberately duller (see 07-25-hud-hover-fold-timing).
    private const int OrbHoverOpenMilliseconds = 280;
    private const int PendingHoverOpenMilliseconds = 280;
    private const int ClassicHoverOpenMilliseconds = 280;
    private const int CompactHoverOpenMilliseconds = 280;
    private const int SessionListHoverCloseMilliseconds = 550;
    private const double MinPendingCardHeight = 220d;
    private const double MinCompletionCardHeight = 260d;
    private const double MinHudDetailHeight = 300d;
    private const double CenterSuctionSourceSize = 8d;
    private const double WorkAreaMargin = 40d;
    private const double TransitionSizeTolerance = 1d;
    private const int PendingTransitionMilliseconds = 210;
    private const int PendingAutoCollapseSeconds = 8;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOCOPYBITS = 0x0100;

    private readonly record struct WindowLayout(
        double Width,
        double Height,
        double Left,
        double Top,
        double PendingHeight,
        double SurfaceHeight,
        double MaxWindowHeight,
        bool PendingOnlyLayout);

    private readonly record struct PhysicalWindowRect(int X, int Y, int Width, int Height);

    private enum ShellTransitionKind
    {
        Expand,
        Shrink,
        SameSize
    }

    private sealed record ShellTransitionPlan(HudMorphPlan MorphPlan, int TransitionId, Action? CompletedAction = null);

    private readonly WpfAppState _state;
    private readonly SettingsManager _settings;
    private readonly HudMorphAnimator _morphAnimator;
    private readonly DispatcherTimer _hoverOpenTimer;
    private readonly DispatcherTimer _hoverCloseTimer;
    private readonly DispatcherTimer _pendingAutoCollapseTimer;
    private readonly DispatcherTimer _transitionGraceTimer;
    private readonly DispatcherTimer _fullscreenTimer;
    private readonly DispatcherTimer _deferredSessionListShrinkTimer;
    private Type? _currentSurfaceType;
    private Type? _currentPendingType;
    private string? _currentPendingKey;
    private bool? _currentCollapsedBarIsVertical;
    private bool _pendingLayerExpanded;
    private bool _hiddenForFullscreen;
    private bool _shellTransitionInProgress;
    private bool _shellTransitionGraceActive;
    private bool _shellPendingBorderActive;
    private bool _renderQueued;
    private bool _renderAfterShellTransition;
    private SolidColorBrush? _shellPendingBorderBrush;
    private PhysicalWindowRect? _lastAppliedPhysicalRect;
    private int _shellTransitionId;
    private bool _shellTransitionDefersHudVisualUpdates;
    private readonly HashSet<string> _exitingHudListItemIds = new(StringComparer.Ordinal);
    private bool _deferredSessionListShrinkReady;
    private bool _sessionListItemExitCompleted;
    private bool _orbDragging;
    private bool _orbDragMoved;
    private System.Windows.Point _orbDragStartScreen;
    private double _orbDragOriginLeft;
    private double _orbDragOriginTop;
    private string _currentHudDensityMode = string.Empty;

    public HudWindow(WpfAppState state, SettingsManager settings)
    {
        InitializeComponent();
        _state = state;
        _settings = settings;
        _morphAnimator = new HudMorphAnimator(Shell, ShellScale, ShellTranslate, ShellSnapshot, SnapshotScale, SnapshotTranslate);
        DataContext = state;

        _hoverOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ClassicHoverOpenMilliseconds) };
        _hoverOpenTimer.Tick += (_, _) =>
        {
            _hoverOpenTimer.Stop();
            if (_state.HasPendingAction)
            {
                if (CanShowFoldablePendingLayer() && !_pendingLayerExpanded && IsPointerInsideHudWindowBounds())
                    SetPendingLayerExpanded(true);
                return;
            }

            if (_state.SurfaceKind == WpfHudSurfaceKind.Collapsed && _state.HasSessions && IsPointerInsideHudWindowBounds())
                _state.ShowSessionList();
        };

        _hoverCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SessionListHoverCloseMilliseconds) };
        _hoverCloseTimer.Tick += (_, _) =>
        {
            _hoverCloseTimer.Stop();
            if (IsShellTransitionProtected())
            {
                ScheduleCloseAfterTransitionIfNeeded();
                return;
            }

            if (!_state.HasPendingAction && _state.SurfaceKind == WpfHudSurfaceKind.SessionList && !IsPointerInsideHudWindowBounds())
                _state.Collapse();
        };

        _pendingAutoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PendingAutoCollapseSeconds) };
        _pendingAutoCollapseTimer.Tick += (_, _) =>
        {
            _pendingAutoCollapseTimer.Stop();
            if (IsShellTransitionProtected())
                return;
            if (_state.IsPendingPinned)
                return;

            if (CanShowFoldablePendingLayer() && _pendingLayerExpanded && !IsPointerInsideHudWindowBounds())
                SetPendingLayerExpanded(false);
        };

        _transitionGraceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PendingTransitionMilliseconds) };
        _transitionGraceTimer.Tick += (_, _) =>
        {
            _transitionGraceTimer.Stop();
            _shellTransitionGraceActive = false;
            ScheduleCloseAfterTransitionIfNeeded();
        };

        _deferredSessionListShrinkTimer = new DispatcherTimer();
        _deferredSessionListShrinkTimer.Tick += (_, _) =>
        {
            _deferredSessionListShrinkTimer.Stop();
            _deferredSessionListShrinkReady = true;
            QueueRender();
        };

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenTimer.Tick += (_, _) => ApplyFullscreenVisibility();
        _fullscreenTimer.Start();

        Loaded += (_, _) =>
        {
            ApplyNoActivate();
            Render();
            PositionWindow();
        };
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        PendingHost.MouseEnter += OnPendingHostMouseEnter;
        PendingHost.MouseLeave += OnPendingHostMouseLeave;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        _state.PropertyChanged += OnStatePropertyChanged;
        _settings.SettingChanged += OnSettingChanged;
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key is "display_position" or "display_monitor" or "hud_density_mode"
            or WpfHudDensityMode.OrbLeftKey or WpfHudDensityMode.OrbTopKey or WpfHudDensityMode.OrbMonitorIdKey)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateCollapsedBarOrientation();
                QueueRender();
            });
        }
        else if (e.Key == "hide_when_fullscreen")
            Dispatcher.Invoke(ApplyFullscreenVisibility);
        else if (e.Key == "panel_height_mode")
            Dispatcher.Invoke(QueueRender);
    }

    public void ShowNoActivate()
    {
        _hiddenForFullscreen = false;
        if (!IsVisible)
        {
            Render();
            Show();
        }
        else
        {
            Render();
        }

        ApplyNoActivate();
        Topmost = true;
    }

    private void RestoreAfterFullscreen()
    {
        if (!IsVisible)
        {
            Render();
            Show();
        }
        else
        {
            Render();
        }

        ApplyNoActivate();
        Topmost = true;
    }

    public void HideHud()
    {
        _hiddenForFullscreen = false;
        _hoverOpenTimer.Stop();
        _hoverCloseTimer.Stop();
        _pendingAutoCollapseTimer.Stop();
        _morphAnimator.Stop(clearClip: true);
        _morphAnimator.ResetShell();
        _transitionGraceTimer.Stop();
        _shellTransitionInProgress = false;
        _shellTransitionGraceActive = false;
        _renderAfterShellTransition = false;
        ReleaseShellTransitionHudVisualUpdateDeferral();
        Hide();
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
            HideHud();
        else
            ShowNoActivate();
    }

    public void ToggleExpanded()
    {
        if (!IsVisible)
            ShowNoActivate();

        if (_state.SurfaceKind == WpfHudSurfaceKind.Collapsed)
        {
            if (!_state.HasPendingAction)
                _state.ShowSessionList();
        }
        else
        {
            _state.Collapse();
        }
    }

    // Tray menu labels; snapshot only at open time.
    public bool IsHudVisible => IsVisible;

    public bool IsHudExpanded =>
        IsVisible && _state.SurfaceKind != WpfHudSurfaceKind.Collapsed;

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WpfAppState.IsPendingPinned) or nameof(WpfAppState.PendingPinButtonText))
        {
            if (_state.IsPendingPinned)
                _pendingAutoCollapseTimer.Stop();
            else
                RestartPendingAutoCollapse();
            return;
        }

        if (_shellTransitionInProgress)
        {
            _renderAfterShellTransition = true;
            return;
        }

        QueueRender();
    }

    private void QueueRender()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(QueueRender, DispatcherPriority.Render);
            return;
        }

        if (_renderQueued)
            return;

        _renderQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _renderQueued = false;
            Render();
        }, DispatcherPriority.Render);
    }

    private void Render()
    {
        _renderQueued = false;
        var previousLayout = CaptureCurrentLayout();
        var previousPendingExpanded = _pendingLayerExpanded;
        var animationSettings = GetHudAnimationSettings();

        var next = _state.SurfaceKind switch
        {
            WpfHudSurfaceKind.SessionList => typeof(SessionListView),
            WpfHudSurfaceKind.HudDetail => typeof(HudDetailView),
            WpfHudSurfaceKind.CompletionCard => typeof(CompletionCardView),
            _ => IsOrbHudMode() ? typeof(FloatingOrbView) : typeof(CollapsedBarView)
        };
        var previousSurfaceType = _currentSurfaceType;
        var surfaceChanged = _currentSurfaceType != next;
        var nextDensityMode = GetCurrentHudDensityMode();
        // First paint has no previous mode; only real setting changes should morph.
        var densityModeChanged = _currentHudDensityMode.Length > 0 &&
            !string.Equals(_currentHudDensityMode, nextDensityMode, StringComparison.Ordinal);
        _currentHudDensityMode = nextDensityMode;
        var expectedPendingExpanded = GetExpectedPendingLayerExpanded();
        var pendingExpandedChanged = previousPendingExpanded != expectedPendingExpanded;
        var useCollapsedSource = ShouldUseCollapsedSource(previousSurfaceType, next);

        if (pendingExpandedChanged)
            _pendingLayerExpanded = expectedPendingExpanded;

        var targetLayout = CalculateWindowLayout();
        var transitionKind = ResolveShellTransitionKind(previousLayout, targetLayout);
        // classic↔compact keeps CollapsedBarView, so surfaceChanged is false; still morph the shell.
        var densityLayoutChanged = densityModeChanged && transitionKind != ShellTransitionKind.SameSize;
        if (densityLayoutChanged && !surfaceChanged)
            useCollapsedSource = false;
        var shouldDeferNonTopSessionListShrink = ShouldDeferNonTopSessionListShrink(previousSurfaceType, next, transitionKind);
        if (shouldDeferNonTopSessionListShrink)
        {
            ApplyRenderedState(next, expectedPendingExpanded, animationSettings);
            ScheduleDeferredSessionListShrink(animationSettings.SurfaceDuration);
            return;
        }

        var shouldAnimateNonTopSessionListShrinkBounds = ShouldAnimateNonTopSessionListShrinkBounds(previousSurfaceType, next, transitionKind);
        if (!shouldAnimateNonTopSessionListShrinkBounds)
            CancelDeferredSessionListShrink();

        var sessionListLayoutChanged = ShouldAnimateSessionListLayoutChange(previousSurfaceType, next, transitionKind);
        var shouldRunShellTransition = surfaceChanged || pendingExpandedChanged || sessionListLayoutChanged || densityLayoutChanged;
        var duration = surfaceChanged || sessionListLayoutChanged || densityLayoutChanged || shouldAnimateNonTopSessionListShrinkBounds
            ? animationSettings.SurfaceDuration
            : animationSettings.PendingDuration;
        if (shouldAnimateNonTopSessionListShrinkBounds)
        {
            CancelDeferredSessionListShrink();
            ApplyRenderedState(next, expectedPendingExpanded, animationSettings);
            StartWindowBoundsTransition(previousLayout, targetLayout, duration);
            return;
        }

        if (shouldRunShellTransition && transitionKind == ShellTransitionKind.Shrink)
        {
            var useSnapshotLayer = !sessionListLayoutChanged;
            var deferTargetContentUntilCompleted = useCollapsedSource ||
                pendingExpandedChanged ||
                (useSnapshotLayer && animationSettings.UsesSnapshotLayerForShrink);
            var revealCollapsedContentAfterShrink = deferTargetContentUntilCompleted &&
                (next == typeof(CollapsedBarView) || next == typeof(FloatingOrbView));
            if (deferTargetContentUntilCompleted)
                _pendingLayerExpanded = previousPendingExpanded;

            var transitionPlan = PrepareShellTransition(
                previousLayout,
                targetLayout,
                duration,
                transitionKind,
                useCollapsedSource,
                useSnapshotLayer: useSnapshotLayer,
                keepShrinkAnchoredToCurrentContent: sessionListLayoutChanged,
                completedAction: () =>
                {
                    if (deferTargetContentUntilCompleted)
                    {
                        ApplyRenderedState(next, expectedPendingExpanded, animationSettings, animateSurfaceContent: false);
                        if (revealCollapsedContentAfterShrink)
                            PrepareCollapsedShrinkRevealAfterSnapshot();
                    }

                    UpdateWindowBounds();
                    Shell.UpdateLayout();
                    if (revealCollapsedContentAfterShrink)
                        QueueCollapsedContentReveal(animationSettings);
                });

            if (!deferTargetContentUntilCompleted)
                ApplyRenderedState(next, expectedPendingExpanded, animationSettings);

            StartPreparedShellTransition(transitionPlan);
            return;
        }

        if (shouldRunShellTransition && transitionKind == ShellTransitionKind.Expand)
        {
            var transitionPlan = PrepareShellTransition(
                previousLayout,
                targetLayout,
                duration,
                transitionKind,
                useCollapsedSource);
            ApplyRenderedState(next, expectedPendingExpanded, animationSettings, animateSurfaceContent: !useCollapsedSource);
            UpdateWindowBoundsAndStartTransition(transitionPlan, transitionKind);
            return;
        }

        ApplyRenderedState(next, expectedPendingExpanded, animationSettings);

        ShellTransitionPlan? plan = null;
        if (shouldRunShellTransition)
            plan = PrepareShellTransition(previousLayout, targetLayout, duration, transitionKind, useCollapsedSource);

        UpdateWindowBoundsAndStartTransition(plan, transitionKind);
    }

    private void ApplyRenderedState(Type surfaceType, bool pendingExpanded, HudAnimationSettings animationSettings, bool animateSurfaceContent = true)
    {
        _pendingLayerExpanded = pendingExpanded;
        RenderPending(animationSettings);
        ApplySurfaceContent(surfaceType, animationSettings, animateSurfaceContent);
        UpdateSurfaceHostPresentation();
    }

    private void ApplySurfaceContent(Type surfaceType, HudAnimationSettings animationSettings, bool animateSurfaceContent)
    {
        var collapsedIsVertical = surfaceType == typeof(CollapsedBarView) && UseSideCollapsedLayout();
        if (_currentSurfaceType != surfaceType)
        {
            _morphAnimator.SwapContent(
                SurfaceHost,
                SurfaceOutgoingHost,
                CreateView(surfaceType, collapsedIsVertical),
                animateSurfaceContent && animationSettings.AllowsContentMotion && SurfaceHost.Content != null,
                animationSettings.ContentDuration,
                animationSettings.ContentSlideOffset);
            _currentSurfaceType = surfaceType;
            _currentCollapsedBarIsVertical = surfaceType == typeof(CollapsedBarView) ? collapsedIsVertical : null;
            return;
        }

        UpdateCollapsedBarOrientation();
    }

    private void PrepareCollapsedShrinkRevealAfterSnapshot()
    {
        SurfaceHost.BeginAnimation(OpacityProperty, null);
        SurfaceHost.Opacity = 0d;
        RootLayer.BeginAnimation(OpacityProperty, null);
        RootLayer.Opacity = 1d;
    }

    private void QueueCollapsedContentReveal(HudAnimationSettings animationSettings)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_state.SurfaceKind != WpfHudSurfaceKind.Collapsed ||
                SurfaceHost.Content is not (CollapsedBarView or FloatingOrbView))
                return;

            _morphAnimator.FadeIn(SurfaceHost, animationSettings.ContentDuration, slideOffset: 0d);
        }, DispatcherPriority.Render);
    }

    private bool GetExpectedPendingLayerExpanded()
    {
        if (!CanShowFoldablePendingLayer())
            return false;

        var nextPendingType = _state.PendingKind switch
        {
            WpfPendingKind.Permission => typeof(ApprovalCardView),
            WpfPendingKind.Question => typeof(QuestionCardView),
            _ => null
        };
        if (nextPendingType == null)
            return false;

        var pendingKey = GetPendingKey();
        var pendingChanged = _currentPendingType != nextPendingType || !string.Equals(_currentPendingKey, pendingKey, StringComparison.Ordinal);
        return pendingChanged || _pendingLayerExpanded;
    }

    private void UpdateSurfaceHostPresentation()
    {
        var pendingOnlyLayout = IsExpandedPendingOnlyLayout();
        SurfaceLayer.Visibility = pendingOnlyLayout ? Visibility.Collapsed : Visibility.Visible;
        PendingHost.Margin = pendingOnlyLayout ? PendingHostOnlyMargin : PendingHostStackedMargin;
    }

    private bool IsExpandedPendingOnlyLayout() => CanShowFoldablePendingLayer() && _pendingLayerExpanded;

    private void UpdateCollapsedBarOrientation()
    {
        if (SurfaceHost.Content is not CollapsedBarView collapsedBar)
            return;

        var collapsedIsVertical = UseSideCollapsedLayout();
        if (_currentCollapsedBarIsVertical == collapsedIsVertical)
            return;

        collapsedBar.IsVertical = collapsedIsVertical;
        _currentCollapsedBarIsVertical = collapsedIsVertical;
    }

    private void RenderPending(HudAnimationSettings animationSettings)
    {
        if (!CanShowFoldablePendingLayer())
        {
            _pendingAutoCollapseTimer.Stop();
            _pendingLayerExpanded = false;
            _currentPendingKey = null;
            HidePendingHost(clearContent: true);
            return;
        }

        var next = _state.PendingKind switch
        {
            WpfPendingKind.Permission => typeof(ApprovalCardView),
            WpfPendingKind.Question => typeof(QuestionCardView),
            _ => null
        };
        if (next == null)
        {
            _pendingAutoCollapseTimer.Stop();
            _pendingLayerExpanded = false;
            _currentPendingKey = null;
            HidePendingHost(clearContent: true);
            return;
        }

        var pendingKey = GetPendingKey();
        var pendingChanged = _currentPendingType != next || !string.Equals(_currentPendingKey, pendingKey, StringComparison.Ordinal);
        if (pendingChanged)
            _pendingLayerExpanded = true;

        _currentPendingKey = pendingKey;

        if (_currentPendingType != next || PendingHost.Content == null)
        {
            PendingHost.Content = CreateView(next);
            _currentPendingType = next;
        }

        if (_pendingLayerExpanded)
        {
            if (PendingHost.Visibility != Visibility.Visible)
                PendingHost.Visibility = Visibility.Visible;
            if (animationSettings.AllowsContentMotion && pendingChanged)
                _morphAnimator.FadeIn(PendingHost, animationSettings.ContentDuration, animationSettings.ContentSlideOffset);
            if (pendingChanged)
                RestartPendingAutoCollapse();
        }
        else
        {
            HidePendingHost(clearContent: false);
        }
    }

    private bool CanShowFoldablePendingLayer() =>
        _state.HasPendingAction && _state.SurfaceKind is not (WpfHudSurfaceKind.SessionList or WpfHudSurfaceKind.HudDetail);

    private string GetPendingKey() => $"{_state.PendingKind}:{_state.PendingActionRevision}";

    private static HudAnimationSettings GetHudAnimationSettings() => HudAnimationSettings.ForCurrentRenderer();

    private bool ShouldAnimateSessionListLayoutChange(Type? previousSurfaceType, Type nextSurfaceType, ShellTransitionKind transitionKind)
    {
        if (previousSurfaceType != typeof(SessionListView) ||
            nextSurfaceType != typeof(SessionListView) ||
            transitionKind == ShellTransitionKind.SameSize)
        {
            return false;
        }

        // Content-only updates (new messages while a row stays expanded) should not re-morph the shell.
        // Still animate real expand (opening list / growing for more rows) and top-anchored shrink.
        return transitionKind == ShellTransitionKind.Expand || IsTopDisplayPosition();
    }

    private bool ShouldDeferNonTopSessionListShrink(Type? previousSurfaceType, Type nextSurfaceType, ShellTransitionKind transitionKind) =>
        IsNonTopSessionListShrink(previousSurfaceType, nextSurfaceType, transitionKind) &&
        !_deferredSessionListShrinkReady &&
        !_sessionListItemExitCompleted;

    private bool ShouldAnimateNonTopSessionListShrinkBounds(Type? previousSurfaceType, Type nextSurfaceType, ShellTransitionKind transitionKind) =>
        IsNonTopSessionListShrink(previousSurfaceType, nextSurfaceType, transitionKind) &&
        (_deferredSessionListShrinkReady || _sessionListItemExitCompleted);

    private bool IsNonTopSessionListShrink(Type? previousSurfaceType, Type nextSurfaceType, ShellTransitionKind transitionKind) =>
        previousSurfaceType == typeof(SessionListView) &&
        nextSurfaceType == typeof(SessionListView) &&
        transitionKind == ShellTransitionKind.Shrink &&
        !IsTopDisplayPosition();

    private void ScheduleDeferredSessionListShrink(Duration duration)
    {
        _deferredSessionListShrinkReady = false;
        _deferredSessionListShrinkTimer.Stop();

        if (!duration.HasTimeSpan || duration.TimeSpan <= TimeSpan.Zero)
        {
            _deferredSessionListShrinkReady = true;
            QueueRender();
            return;
        }

        _deferredSessionListShrinkTimer.Interval = duration.TimeSpan;
        _deferredSessionListShrinkTimer.Start();
    }

    private void CancelDeferredSessionListShrink()
    {
        _deferredSessionListShrinkTimer.Stop();
        _deferredSessionListShrinkReady = false;
        _sessionListItemExitCompleted = false;
    }

    internal void BeginSessionListItemExitAnimation(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        if (IsTopDisplayPosition() && _exitingHudListItemIds.Add(itemId))
            QueueRenderAfterSessionListExitStateChanged();
    }

    internal void CompleteSessionListItemExitAnimation(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        _sessionListItemExitCompleted = true;
        if (_exitingHudListItemIds.Remove(itemId))
            QueueRenderAfterSessionListExitStateChanged();
    }

    private void QueueRenderAfterSessionListExitStateChanged()
    {
        if (IsShellTransitionProtected())
        {
            _renderAfterShellTransition = true;
            return;
        }

        QueueRender();
    }

    private void SetPendingLayerExpanded(bool expanded)
    {
        if (!CanShowFoldablePendingLayer() || _pendingLayerExpanded == expanded)
            return;

        var animationSettings = GetHudAnimationSettings();
        var previousLayout = CaptureCurrentLayout();
        _pendingLayerExpanded = expanded;
        var targetLayout = CalculateWindowLayout();
        var transitionKind = ResolveShellTransitionKind(previousLayout, targetLayout);

        // Collapse pending: deterministic hard land (no shrink morph / empty-shell races).
        if (transitionKind == ShellTransitionKind.Shrink)
        {
            _morphAnimator.Stop(clearClip: true);
            _morphAnimator.ResetShell();
            RenderPending(animationSettings);
            UpdateCollapsedBarOrientation();
            UpdateSurfaceHostPresentation();
            UpdateWindowBounds();
            Shell.UpdateLayout();
            return;
        }

        if (transitionKind == ShellTransitionKind.Expand)
        {
            var transitionPlan = PrepareShellTransition(
                previousLayout,
                targetLayout,
                animationSettings.PendingDuration,
                transitionKind,
                useCollapsedSource: true);
            RenderPending(animationSettings);
            UpdateCollapsedBarOrientation();
            UpdateSurfaceHostPresentation();
            UpdateWindowBoundsAndStartTransition(transitionPlan, transitionKind);
            return;
        }

        RenderPending(animationSettings);
        UpdateCollapsedBarOrientation();
        UpdateSurfaceHostPresentation();
        var plan = PrepareShellTransition(previousLayout, targetLayout, animationSettings.PendingDuration, transitionKind, useCollapsedSource: true);
        UpdateWindowBoundsAndStartTransition(plan, transitionKind);
    }

    private void HidePendingHost(bool clearContent)
    {
        PendingHost.BeginAnimation(OpacityProperty, null);
        PendingHost.Opacity = 1d;
        if (PendingHost.Visibility != Visibility.Collapsed)
            PendingHost.Visibility = Visibility.Collapsed;

        if (!clearContent)
            return;

        PendingHost.Content = null;
        _currentPendingType = null;
    }

    private object CreateView(Type type, bool collapsedIsVertical = false)
    {
        if (type == typeof(CollapsedBarView))
            return new CollapsedBarView { IsVertical = collapsedIsVertical };
        if (type == typeof(FloatingOrbView))
            return new FloatingOrbView();

        return Activator.CreateInstance(type)!;
    }

    private WindowLayout CaptureCurrentLayout()
    {
        var width = GetCurrentWindowWidth();
        var height = GetCurrentWindowHeight();
        var left = !double.IsNaN(Left) ? Left : 0d;
        var top = !double.IsNaN(Top) ? Top : 0d;
        return new WindowLayout(width, height, left, top, 0d, 0d, GetMaxWindowHeight(), IsExpandedPendingOnlyLayout());
    }

    private double GetCurrentWindowWidth() => ActualWidth > 0 ? ActualWidth : (!double.IsNaN(Width) && Width > 0 ? Width : 0d);

    private double GetCurrentWindowHeight() => ActualHeight > 0 ? ActualHeight : (!double.IsNaN(Height) && Height > 0 ? Height : 0d);

    private ShellTransitionPlan? PrepareShellTransition(
        WindowLayout previousLayout,
        WindowLayout targetLayout,
        Duration duration,
        ShellTransitionKind transitionKind,
        bool useCollapsedSource,
        bool useSnapshotLayer = true,
        bool keepShrinkAnchoredToCurrentContent = false,
        Action? completedAction = null)
    {
        _morphAnimator.Stop();
        var transitionId = BeginShellTransitionProtection();
        var animationSettings = GetHudAnimationSettings();
        if (!animationSettings.AllowsShellMorph ||
            previousLayout.Width <= 0 || previousLayout.Height <= 0 || targetLayout.Width <= 0 || targetLayout.Height <= 0)
        {
            CompleteShellTransitionProtection(transitionId);
            completedAction?.Invoke();
            return null;
        }

        if (transitionKind == ShellTransitionKind.SameSize)
        {
            var pulsePlan = new HudMorphPlan(
                null,
                Rect.Empty,
                null,
                new Rect(0d, 0d, targetLayout.Width, targetLayout.Height),
                duration,
                UseClip: false,
                new System.Windows.Point(0.5d, 0d),
                InitialOpacity: 0.97d,
                InitialScale: 0.99d);
            _morphAnimator.Prepare(pulsePlan);
            return new ShellTransitionPlan(pulsePlan, transitionId, completedAction);
        }

        var (initialRect, midRect, finalRect) = CalculateShellMorphRects(
            previousLayout,
            targetLayout,
            transitionKind,
            useCollapsedSource,
            keepShrinkAnchoredToCurrentContent);
        var initialClipRadius = CalculateMorphClipRadius(initialRect, GetCurrentShellClipRadius());
        var midClipRadius = midRect is { } resolvedMidRect
            ? CalculateMorphClipRadius(resolvedMidRect, GetShellCornerRadius().TopLeft)
            : (double?)null;
        var finalClipRadius = CalculateMorphClipRadius(finalRect, GetShellCornerRadius().TopLeft);
        var completionClipRect = new Rect(0d, 0d, Math.Max(1d, targetLayout.Width), Math.Max(1d, targetLayout.Height));
        var clip = new RectangleGeometry(initialRect, initialClipRadius, initialClipRadius);
        var morphPlan = new HudMorphPlan(
            clip,
            initialRect,
            midRect,
            finalRect,
            duration,
            UseClip: true,
            transitionKind == ShellTransitionKind.Shrink
                ? GetTransitionOrigin(finalRect, initialRect)
                : GetTransitionOrigin(initialRect, finalRect),
            InitialClipRadius: initialClipRadius,
            MidClipRadius: midClipRadius,
            FinalClipRadius: finalClipRadius,
            MidKeyTime: CalculateMorphMidKeyTime(duration, transitionKind),
            CompletionClipRect: completionClipRect,
            CompletionClipRadius: CalculateMorphClipRadius(completionClipRect, GetShellCornerRadius().TopLeft),
            UseSnapshotLayer: useSnapshotLayer && transitionKind == ShellTransitionKind.Shrink && animationSettings.UsesSnapshotLayerForShrink);
        _morphAnimator.Prepare(morphPlan);
        return new ShellTransitionPlan(morphPlan, transitionId, completedAction);
    }

    private static bool ShouldUseCollapsedSource(Type? previousSurfaceType, Type nextSurfaceType) =>
        previousSurfaceType == typeof(CollapsedBarView) ||
        nextSurfaceType == typeof(CollapsedBarView) ||
        previousSurfaceType == typeof(FloatingOrbView) ||
        nextSurfaceType == typeof(FloatingOrbView);

    private (Rect InitialRect, Rect? MidRect, Rect FinalRect) CalculateShellMorphRects(
        WindowLayout previousLayout,
        WindowLayout targetLayout,
        ShellTransitionKind transitionKind,
        bool useCollapsedSource,
        bool keepShrinkAnchoredToCurrentContent)
    {
        if (!useCollapsedSource)
        {
            if (transitionKind == ShellTransitionKind.Shrink && keepShrinkAnchoredToCurrentContent)
            {
                return (
                    new Rect(0d, 0d, Math.Max(1d, previousLayout.Width), Math.Max(1d, previousLayout.Height)),
                    null,
                    new Rect(0d, 0d, Math.Max(1d, targetLayout.Width), Math.Max(1d, targetLayout.Height)));
            }

            return transitionKind == ShellTransitionKind.Shrink
                ? (
                    new Rect(0d, 0d, Math.Max(1d, previousLayout.Width), Math.Max(1d, previousLayout.Height)),
                    null,
                    CalculateRelativeSourceRect(targetLayout, previousLayout))
                : (
                    CalculateRelativeSourceRect(previousLayout, targetLayout),
                    null,
                    new Rect(0d, 0d, Math.Max(1d, targetLayout.Width), Math.Max(1d, targetLayout.Height)));
        }

        var collapsedSourceLayout = CalculateCollapsedSourceLayout();
        if (transitionKind == ShellTransitionKind.Shrink)
        {
            var collapsedRect = CalculateRelativeSourceRect(collapsedSourceLayout, previousLayout);
            return (
                new Rect(0d, 0d, Math.Max(1d, previousLayout.Width), Math.Max(1d, previousLayout.Height)),
                collapsedRect,
                CalculateCenterSuctionSourceRect(collapsedRect));
        }

        var sourceRect = CalculateRelativeSourceRect(collapsedSourceLayout, targetLayout);
        return (
            CalculateCenterSuctionSourceRect(sourceRect),
            sourceRect,
            new Rect(0d, 0d, Math.Max(1d, targetLayout.Width), Math.Max(1d, targetLayout.Height)));
    }

    private void UpdateWindowBoundsAndStartTransition(ShellTransitionPlan? plan, ShellTransitionKind transitionKind)
    {
        if (transitionKind == ShellTransitionKind.Expand && plan?.MorphPlan.UseClip == true)
        {
            // Move the HWND once, lay out the target content, then reveal in the same dispatcher turn.
            UpdateWindowBounds();
            Shell.UpdateLayout();
            if (plan.TransitionId == _shellTransitionId && _shellTransitionInProgress)
                StartPreparedShellTransition(plan);
            return;
        }

        UpdateWindowBounds();
        if (plan is not null)
            StartPreparedShellTransition(plan);
    }

    private void StartWindowBoundsTransition(WindowLayout previousLayout, WindowLayout targetLayout, Duration duration)
    {
        _morphAnimator.Stop(clearClip: true);
        var transitionId = BeginShellTransitionProtection();
        if (!duration.HasTimeSpan ||
            duration.TimeSpan <= TimeSpan.Zero ||
            previousLayout.Width <= 0 ||
            previousLayout.Height <= 0 ||
            targetLayout.Width <= 0 ||
            targetLayout.Height <= 0)
        {
            ApplyWindowContentLayout(targetLayout);
            ApplyWindowLayout(targetLayout);
            Shell.UpdateLayout();
            CompleteShellTransitionProtection(transitionId);
            return;
        }

        ClearWindowLayoutAnimations();
        Width = previousLayout.Width;
        Height = previousLayout.Height;
        Left = previousLayout.Left;
        Top = previousLayout.Top;

        ApplyWindowContentLayout(targetLayout);
        Shell.UpdateLayout();

        var heightAnimation = CreateWindowBoundsAnimation(targetLayout.Height, duration);
        heightAnimation.Completed += (_, _) => CompleteWindowBoundsTransition(transitionId, targetLayout);

        BeginAnimation(WidthProperty, CreateWindowBoundsAnimation(targetLayout.Width, duration));
        BeginAnimation(HeightProperty, heightAnimation);
        BeginAnimation(LeftProperty, CreateWindowBoundsAnimation(targetLayout.Left, duration));
        BeginAnimation(TopProperty, CreateWindowBoundsAnimation(targetLayout.Top, duration));
    }

    private void CompleteWindowBoundsTransition(int transitionId, WindowLayout targetLayout)
    {
        if (transitionId != _shellTransitionId)
            return;

        ClearWindowLayoutAnimations();
        ApplyWindowContentLayout(targetLayout);
        ApplyWindowLayout(targetLayout);
        Shell.UpdateLayout();
        CompleteShellTransitionProtection(transitionId);
    }

    private static DoubleAnimation CreateWindowBoundsAnimation(double to, Duration duration) =>
        new(to, duration)
        {
            EasingFunction = new HudShellMorphEase(),
            FillBehavior = FillBehavior.HoldEnd
        };

    private void ClearWindowLayoutAnimations()
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
    }

    private void StartPreparedShellTransition(ShellTransitionPlan? plan)
    {
        if (plan is null)
            return;

        var easing = CreateShellTransitionEasing(plan.MorphPlan);
        _morphAnimator.Start(plan.MorphPlan, easing, () =>
        {
            plan.CompletedAction?.Invoke();
            CompleteShellTransitionProtection(plan.TransitionId);
        });
    }

    private static IEasingFunction CreateShellTransitionEasing(HudMorphPlan plan) =>
        plan.UseClip ? new HudShellMorphEase() : new CubicEase { EasingMode = EasingMode.EaseOut };

    private ShellTransitionKind ResolveShellTransitionKind(WindowLayout previousLayout, WindowLayout targetLayout)
    {
        var widthChanged = Math.Abs(previousLayout.Width - targetLayout.Width) > TransitionSizeTolerance;
        var heightChanged = Math.Abs(previousLayout.Height - targetLayout.Height) > TransitionSizeTolerance;
        if (!widthChanged && !heightChanged)
            return ShellTransitionKind.SameSize;

        return targetLayout.Width < previousLayout.Width || targetLayout.Height < previousLayout.Height
            ? ShellTransitionKind.Shrink
            : ShellTransitionKind.Expand;
    }

    private WindowLayout CalculateCollapsedSourceLayout()
    {
        if (IsOrbHudMode())
        {
            var orbWidth = GetCollapsedHorizontalWidth();
            var orbHeight = GetCollapsedHorizontalHeight();
            var (orbLeft, orbTop) = ResolveOrbWindowPosition(orbWidth, orbHeight);
            return new WindowLayout(
                orbWidth,
                orbHeight,
                orbLeft,
                orbTop,
                PendingHeight: 0d,
                SurfaceHeight: Math.Max(0d, orbHeight - GetShellPadding()),
                MaxWindowHeight: GetMaxWindowHeight(),
                PendingOnlyLayout: false);
        }

        var sideCollapsed = IsSideCenterPosition();
        var width = sideCollapsed ? GetCollapsedSideWidth() : GetCollapsedHorizontalWidth();
        var height = sideCollapsed ? GetCollapsedSideHeight() : GetCollapsedHorizontalHeight();
        var shellPadding = IsCompactHudMode() ? CompactCollapsedShellPadding : ShellPadding;
        var (left, top) = CalculateWindowPosition(width, height);
        return new WindowLayout(
            width,
            height,
            left,
            top,
            PendingHeight: 0d,
            SurfaceHeight: Math.Max(0d, height - shellPadding),
            MaxWindowHeight: GetMaxWindowHeight(),
            PendingOnlyLayout: false);
    }

    private static Rect CalculateRelativeSourceRect(WindowLayout sourceLayout, WindowLayout containerLayout)
    {
        var width = Math.Clamp(sourceLayout.Width, 1d, Math.Max(1d, containerLayout.Width));
        var height = Math.Clamp(sourceLayout.Height, 1d, Math.Max(1d, containerLayout.Height));
        var x = ClampRelativeCoordinate(sourceLayout.Left - containerLayout.Left, containerLayout.Width, width);
        var y = ClampRelativeCoordinate(sourceLayout.Top - containerLayout.Top, containerLayout.Height, height);

        return new Rect(x, y, width, height);
    }

    private static Rect CalculateCenterSuctionSourceRect(Rect sourceRect)
    {
        var size = Math.Clamp(CenterSuctionSourceSize, 1d, Math.Max(1d, Math.Min(sourceRect.Width, sourceRect.Height)));
        return new Rect(
            sourceRect.Left + (sourceRect.Width - size) / 2d,
            sourceRect.Top + (sourceRect.Height - size) / 2d,
            size,
            size);
    }

    private static TimeSpan? CalculateMorphMidKeyTime(Duration duration, ShellTransitionKind transitionKind)
    {
        if (!duration.HasTimeSpan)
            return null;

        var progress = transitionKind == ShellTransitionKind.Shrink ? 0.70d : 0.30d;
        return TimeSpan.FromMilliseconds(Math.Max(1d, duration.TimeSpan.TotalMilliseconds * progress));
    }

    private static double ClampRelativeCoordinate(double coordinate, double containerSize, double contentSize)
    {
        var max = Math.Max(0d, containerSize - contentSize);
        if (double.IsNaN(coordinate) || double.IsInfinity(coordinate))
            return 0d;

        return Math.Clamp(coordinate, 0d, max);
    }

    private static System.Windows.Point GetTransitionOrigin(Rect initialRect, Rect finalRect)
    {
        var originX = finalRect.Width <= 0 ? 0.5d : Math.Clamp((initialRect.Left + initialRect.Width / 2d) / finalRect.Width, 0d, 1d);
        var originY = finalRect.Height <= 0 ? 0d : Math.Clamp((initialRect.Top + initialRect.Height / 2d) / finalRect.Height, 0d, 1d);
        return new System.Windows.Point(originX, originY);
    }

    private double GetCurrentShellClipRadius()
    {
        var radius = Shell.CornerRadius.TopLeft;
        return radius > 0d ? radius : GetShellCornerRadius().TopLeft;
    }

    private static double CalculateMorphClipRadius(Rect rect, double shellRadius)
    {
        var shortestSide = Math.Min(rect.Width, rect.Height);
        if (double.IsNaN(shortestSide) || double.IsInfinity(shortestSide) || shortestSide <= 0d)
            return 0d;

        return Math.Min(Math.Max(0d, shellRadius), shortestSide / 2d);
    }

    private int BeginShellTransitionProtection()
    {
        _hoverCloseTimer.Stop();
        _pendingAutoCollapseTimer.Stop();
        _transitionGraceTimer.Stop();
        _shellTransitionGraceActive = false;
        _shellTransitionInProgress = true;
        if (!_shellTransitionDefersHudVisualUpdates)
        {
            _state.BeginHudVisualUpdateDeferral();
            _shellTransitionDefersHudVisualUpdates = true;
        }
        return ++_shellTransitionId;
    }

    private bool IsShellTransitionProtected() => _shellTransitionInProgress || _shellTransitionGraceActive;

    private void CompleteShellTransitionProtection(int transitionId)
    {
        if (transitionId != _shellTransitionId)
            return;

        _shellTransitionInProgress = false;
        _shellTransitionGraceActive = true;
        _transitionGraceTimer.Stop();
        _transitionGraceTimer.Start();
        ReleaseShellTransitionHudVisualUpdateDeferral();
        if (_renderAfterShellTransition)
        {
            _renderAfterShellTransition = false;
            QueueRender();
        }
    }

    private void ReleaseShellTransitionHudVisualUpdateDeferral()
    {
        if (!_shellTransitionDefersHudVisualUpdates)
            return;

        _shellTransitionDefersHudVisualUpdates = false;
        _state.EndHudVisualUpdateDeferral();
    }

    private void ScheduleCloseAfterTransitionIfNeeded()
    {
        if (IsShellTransitionProtected())
            return;

        if (_state.HasPendingAction)
        {
            if (CanShowFoldablePendingLayer() && _pendingLayerExpanded && !IsPointerInsideHudWindowBounds())
                RestartPendingAutoCollapse();
            return;
        }

        if (_state.SurfaceKind == WpfHudSurfaceKind.SessionList && !IsPointerInsideHudWindowBounds())
            _hoverCloseTimer.Start();
    }

    private void RestartPendingAutoCollapse()
    {
        _pendingAutoCollapseTimer.Stop();
        if (_state.IsPendingPinned)
            return;
        if (CanShowFoldablePendingLayer() && _pendingLayerExpanded && !IsPointerInsideHudWindowBounds())
            _pendingAutoCollapseTimer.Start();
    }

    private void UpdateWindowBounds()
    {
        var layout = CalculateWindowLayout();
        ApplyWindowContentLayout(layout);
        ApplyWindowLayout(layout);
    }

    private void ApplyWindowContentLayout(WindowLayout layout)
    {
        var shellPadding = GetShellPadding();
        var contentWidth = Math.Max(0d, layout.Width - shellPadding);

        ApplyShellChrome();
        PendingHost.Width = contentWidth;
        PendingHost.Height = layout.PendingHeight > 0 ? layout.PendingHeight : 0d;
        PendingHost.MaxHeight = layout.PendingHeight > 0 ? layout.PendingHeight : 0d;
        SurfaceLayer.Width = contentWidth;
        SurfaceLayer.Height = layout.PendingOnlyLayout ? 0d : layout.SurfaceHeight;
        SurfaceLayer.MaxHeight = layout.PendingOnlyLayout ? 0d : layout.SurfaceHeight;
        SurfaceHost.Width = contentWidth;
        SurfaceHost.Height = layout.PendingOnlyLayout ? 0d : layout.SurfaceHeight;
        SurfaceHost.MaxHeight = layout.PendingOnlyLayout ? 0d : layout.SurfaceHeight;
        SurfaceOutgoingHost.Width = contentWidth;
        SurfaceOutgoingHost.Height = layout.PendingOnlyLayout ? 0d : layout.SurfaceHeight;
        SurfaceOutgoingHost.MaxHeight = layout.PendingOnlyLayout ? 0d : layout.SurfaceHeight;
        UpdateSurfaceHostPresentation();
        Shell.MaxHeight = Math.Max(GetCollapsedHorizontalHeight(), layout.MaxWindowHeight);
    }

    private WindowLayout CalculateWindowLayout()
    {
        var maxWindowHeight = GetMaxWindowHeight();
        var shellPadding = GetShellPadding();
        var availableContentHeight = Math.Max(GetCollapsedHorizontalHeight(), maxWindowHeight - shellPadding);
        var pendingSize = CalculatePendingSize(availableContentHeight);
        var pendingOnlyLayout = IsExpandedPendingOnlyLayout();
        var surfaceSize = pendingOnlyLayout
            ? (Width: 0d, Height: 0d)
            : CalculateSurfaceSize(availableContentHeight, pendingSize.Height);
        var contentWidth = Math.Max(surfaceSize.Width, pendingSize.Width);
        var contentHeight = pendingOnlyLayout
            ? pendingSize.Height
            : surfaceSize.Height + (pendingSize.Height > 0 ? PendingLayerGap + pendingSize.Height : 0);
        var width = contentWidth + shellPadding;
        var height = Math.Min(contentHeight + shellPadding, maxWindowHeight);
        var (left, top) = CalculateWindowPosition(width, height);
        return new WindowLayout(width, height, left, top, pendingSize.Height, surfaceSize.Height, maxWindowHeight, pendingOnlyLayout);
    }

    private void ApplyWindowLayout(WindowLayout layout)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var physicalRect = handle != IntPtr.Zero
            ? CalculatePhysicalWindowRect(handle, layout)
            : default;

        if (double.IsNaN(Width) || Math.Abs(Width - layout.Width) > TransitionSizeTolerance)
            Width = layout.Width;
        if (double.IsNaN(Height) || Math.Abs(Height - layout.Height) > TransitionSizeTolerance)
            Height = layout.Height;
        if (double.IsNaN(Left) || Math.Abs(Left - layout.Left) > TransitionSizeTolerance)
            Left = layout.Left;
        if (double.IsNaN(Top) || Math.Abs(Top - layout.Top) > TransitionSizeTolerance)
            Top = layout.Top;

        if (handle != IntPtr.Zero && _lastAppliedPhysicalRect != physicalRect)
        {
            var flags = SWP_NOACTIVATE | SWP_SHOWWINDOW;
            if (_lastAppliedPhysicalRect is { } lastAppliedPhysicalRect &&
                (lastAppliedPhysicalRect.Width != physicalRect.Width || lastAppliedPhysicalRect.Height != physicalRect.Height))
                flags |= SWP_NOCOPYBITS;

            SetWindowPos(
                handle,
                HwndTopmost,
                physicalRect.X,
                physicalRect.Y,
                physicalRect.Width,
                physicalRect.Height,
                flags);
            _lastAppliedPhysicalRect = physicalRect;
        }
    }

    private PhysicalWindowRect CalculatePhysicalWindowRect(IntPtr handle, WindowLayout layout)
    {
        var dpi = GetDpiForWindowLayout(handle, layout);
        return new PhysicalWindowRect(
            (int)Math.Round(layout.Left * dpi.DpiScaleX),
            (int)Math.Round(layout.Top * dpi.DpiScaleY),
            (int)Math.Round(layout.Width * dpi.DpiScaleX),
            (int)Math.Round(layout.Height * dpi.DpiScaleY));
    }

    private (double Width, double Height) CalculateSurfaceSize(double availableContentHeight, double pendingHeight)
    {
        var shellPadding = GetShellPadding();
        var collapsedHeight = UseSideCollapsedLayout() ? GetCollapsedSideHeight() - shellPadding : GetCollapsedHorizontalHeight() - shellPadding;
        var remainingHeight = Math.Max(collapsedHeight, availableContentHeight - (pendingHeight > 0 ? pendingHeight + PendingLayerGap : 0));
        return _state.SurfaceKind switch
        {
            WpfHudSurfaceKind.SessionList => (GetHudContentWidth(), CalculateSessionListHeight(remainingHeight)),
            WpfHudSurfaceKind.HudDetail => (GetHudContentWidth(), Math.Clamp(Math.Min(420d, remainingHeight), Math.Min(MinHudDetailHeight, remainingHeight), remainingHeight)),
            WpfHudSurfaceKind.CompletionCard => (GetHudContentWidth(), CalculateCompletionCardHeight(remainingHeight)),
            _ => UseSideCollapsedLayout() ? (GetCollapsedSideWidth() - shellPadding, GetCollapsedSideHeight() - shellPadding) : (GetCollapsedHorizontalWidth() - shellPadding, GetCollapsedHorizontalHeight() - shellPadding)
        };
    }

    private double GetShellPadding()
    {
        if (UseOrbCollapsedChrome())
            return OrbShellPadding;
        return UseCompactCollapsedChrome() ? CompactCollapsedShellPadding : ShellPadding;
    }

    private bool UseCompactCollapsedChrome() =>
        IsCompactHudMode() && _state.SurfaceKind == WpfHudSurfaceKind.Collapsed && !_pendingLayerExpanded;

    private bool UseOrbCollapsedChrome() =>
        IsOrbHudMode() && _state.SurfaceKind == WpfHudSurfaceKind.Collapsed && !_pendingLayerExpanded;

    private void ApplyShellChrome()
    {
        // Orb collapsed: no dark panel chrome. Pending color lives on FloatingOrbView, not Shell rim.
        if (UseOrbCollapsedChrome())
        {
            Shell.Padding = new Thickness(0d);
            Shell.BorderThickness = new Thickness(0d);
            Shell.Background = System.Windows.Media.Brushes.Transparent;
            Shell.BorderBrush = System.Windows.Media.Brushes.Transparent;
            Shell.CornerRadius = new CornerRadius(0d);
            Shell.ClipToBounds = false;
            SurfaceLayer.ClipToBounds = false;
            StopShellPendingBorderAnimation();
            return;
        }

        Shell.ClipToBounds = false;
        SurfaceLayer.ClipToBounds = false;

        var shellPadding = GetShellPadding();
        var borderThickness = _state.ShouldShowPendingAlert ? 2d : 1d;
        var perSideInset = shellPadding / 2d;

        Shell.Padding = new Thickness(Math.Max(0d, perSideInset - borderThickness));
        Shell.BorderThickness = new Thickness(borderThickness);
        Shell.Background = (System.Windows.Media.Brush)FindResource("HudBackgroundBrush");
        Shell.CornerRadius = GetShellCornerRadius();

        if (_state.ShouldShowPendingAlert)
            StartShellPendingBorderAnimation();
        else
            StopShellPendingBorderAnimation();
    }

    private CornerRadius GetShellCornerRadius()
    {
        if (UseOrbCollapsedChrome())
            return new CornerRadius(OrbCornerRadius);
        return UseCompactCollapsedChrome() && UseSideCollapsedLayout() ? new CornerRadius(21d) : new CornerRadius(18d);
    }

    private void StartShellPendingBorderAnimation()
    {
        if (_shellPendingBorderActive)
            return;

        _shellPendingBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB8, 0x6B));
        Shell.BorderBrush = _shellPendingBorderBrush;

        var animation = new ColorAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromSeconds(2.4d)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new SplineColorKeyFrame(System.Windows.Media.Color.FromRgb(0xFF, 0x5F, 0x8F), KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineColorKeyFrame(System.Windows.Media.Color.FromRgb(0xFF, 0xB8, 0x6B), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8d))));
        animation.KeyFrames.Add(new SplineColorKeyFrame(System.Windows.Media.Color.FromRgb(0x8E, 0xE6, 0xD0), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6d))));
        animation.KeyFrames.Add(new SplineColorKeyFrame(System.Windows.Media.Color.FromRgb(0xFF, 0x5F, 0x8F), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4d))));
        _shellPendingBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        _shellPendingBorderActive = true;
    }

    private void StopShellPendingBorderAnimation()
    {
        if (_shellPendingBorderBrush is not null)
            _shellPendingBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);

        _shellPendingBorderBrush = null;
        _shellPendingBorderActive = false;
        Shell.BorderBrush = (System.Windows.Media.Brush)FindResource("HudBorderBrush");
    }

    private double GetCollapsedHorizontalWidth() =>
        IsOrbHudMode() ? OrbCollapsedSize : IsCompactHudMode() ? CompactCollapsedHorizontalWidth : CollapsedHorizontalWidth;

    private double GetCollapsedHorizontalHeight() =>
        IsOrbHudMode() ? OrbCollapsedSize : IsCompactHudMode() ? CompactCollapsedHorizontalHeight : CollapsedHorizontalHeight;

    private double GetCollapsedSideWidth() => IsCompactHudMode() ? CompactCollapsedSideWidth : CollapsedSideWidth;

    private double GetCollapsedSideHeight() => IsCompactHudMode() ? CompactCollapsedSideHeight : CollapsedSideHeight;

    private double GetHudContentWidth() => UsesCompactExpandedMetrics() ? CompactHudContentWidth : HudContentWidth;

    private string GetCurrentHudDensityMode() =>
        WpfHudDensityMode.Normalize(_settings.Get("hud_density_mode", WpfHudDensityMode.Default));

    private bool IsCompactHudMode() => WpfHudDensityMode.IsCompact(GetCurrentHudDensityMode());

    private bool IsOrbHudMode() => WpfHudDensityMode.IsOrb(GetCurrentHudDensityMode());

    private bool UsesCompactExpandedMetrics() =>
        WpfHudDensityMode.UsesCompactExpandedMetrics(GetCurrentHudDensityMode());

    private double CalculateSessionListHeight(double maxSurfaceHeight)
    {
        var itemCount = CountEffectiveHudListItems();
        if (itemCount <= 0)
            return Math.Clamp(120d, 104d, maxSurfaceHeight);

        var compactHud = UsesCompactExpandedMetrics();
        var visibleCardCount = Math.Min(itemCount, compactHud ? 3 : 4);
        var visibleGroupCount = CountVisibleHudListGroups(visibleCardCount);
        var inlineDetailHeight = HasEffectiveExpandedHudListSessionDetail() ? HudAnimationTimings.InlineSessionDetailHeight : 0d;
        var desiredHeight = compactHud
            ? 74d + visibleGroupCount * 30d + visibleCardCount * 72d + inlineDetailHeight
            : 88d + visibleGroupCount * 34d + visibleCardCount * 84d + inlineDetailHeight;
        var mode = _settings.Get("panel_height_mode", "auto");
        return mode switch
        {
            "fixed" => Math.Clamp(compactHud ? 360d : 420d, compactHud ? 210d : 240d, maxSurfaceHeight),
            "compact" => Math.Clamp(
                compactHud ? 64d + visibleGroupCount * 28d + visibleCardCount * 68d + inlineDetailHeight : 74d + visibleGroupCount * 30d + visibleCardCount * 78d + inlineDetailHeight,
                compactHud ? 150d : 170d,
                Math.Min(maxSurfaceHeight, compactHud ? 360d : 420d)),
            _ => Math.Clamp(desiredHeight, compactHud ? 160d : 180d, compactHud ? Math.Min(maxSurfaceHeight, 380d) : maxSurfaceHeight)
        };
    }

    private int CountEffectiveHudListItems()
    {
        if (_exitingHudListItemIds.Count == 0)
            return _state.HudListItems.Count;

        return _state.HudListItems.Count(item => !_exitingHudListItemIds.Contains(item.ItemId));
    }

    private bool HasEffectiveExpandedHudListSessionDetail()
    {
        if (!_state.HasExpandedHudListSessionDetail)
            return false;

        return _state.SelectedHudItem is not { } item || !_exitingHudListItemIds.Contains(item.ItemId);
    }

    private int CountVisibleHudListGroups(int visibleCardCount)
    {
        var remainingCards = visibleCardCount;
        var visibleGroupCount = 0;

        foreach (var group in _state.HudListGroups)
        {
            if (remainingCards <= 0)
                break;

            var groupItemCount = CountEffectiveHudListGroupItems(group);
            if (groupItemCount == 0)
                continue;

            visibleGroupCount++;
            remainingCards -= Math.Min(groupItemCount, remainingCards);
        }

        return visibleGroupCount;
    }

    private int CountEffectiveHudListGroupItems(WpfHudListGroupViewModel group)
    {
        if (_exitingHudListItemIds.Count == 0)
            return group.Items.Count;

        return group.Items.Count(item => !_exitingHudListItemIds.Contains(item.ItemId));
    }

    private (double Width, double Height) CalculatePendingSize(double availableContentHeight)
    {
        if (!_state.HasPendingAction || !_pendingLayerExpanded || _state.SurfaceKind is WpfHudSurfaceKind.SessionList or WpfHudSurfaceKind.HudDetail)
            return (0d, 0d);

        var shellPadding = GetShellPadding();
        var collapsedHeight = UseSideCollapsedLayout() ? GetCollapsedSideHeight() - shellPadding : GetCollapsedHorizontalHeight() - shellPadding;
        var maxPendingHeight = IsExpandedPendingOnlyLayout()
            ? availableContentHeight
            : Math.Max(MinPendingCardHeight, availableContentHeight - collapsedHeight - PendingLayerGap);
        return _state.PendingKind switch
        {
            WpfPendingKind.Permission => (GetHudContentWidth(), CalculateApprovalCardHeight(maxPendingHeight)),
            WpfPendingKind.Question => (GetHudContentWidth(), CalculateQuestionCardHeight(maxPendingHeight)),
            _ => (0d, 0d)
        };
    }

    private double CalculateApprovalCardHeight(double maxPendingHeight)
    {
        var command = _state.PermissionCommand ?? string.Empty;
        var estimatedCommandLines = EstimateWrappedLineCount(command, 68);
        var estimated = 216d + Math.Min(220d, estimatedCommandLines * 16d);
        return Math.Clamp(estimated, Math.Min(MinPendingCardHeight, maxPendingHeight), maxPendingHeight);
    }

    private double CalculateQuestionCardHeight(double maxPendingHeight)
    {
        var estimatedQuestionLines = EstimateWrappedLineCount(_state.QuestionText ?? string.Empty, 70);
        var optionLines = _state.QuestionOptions.Count == 0
            ? 4d
            : _state.QuestionOptions.Take(6).Sum(option => 2d + EstimateWrappedLineCount(option.Label, 62) + (option.HasDescription ? EstimateWrappedLineCount(option.Description, 68) : 0));
        var estimated = 210d + Math.Min(160d, estimatedQuestionLines * 16d) + Math.Min(240d, optionLines * 18d);
        return Math.Clamp(estimated, Math.Min(MinPendingCardHeight, maxPendingHeight), maxPendingHeight);
    }

    private double CalculateCompletionCardHeight(double maxSurfaceHeight)
    {
        var promptLines = _state.HasCompletionUserPrompt ? EstimateWrappedLineCount(_state.CompletionUserPrompt ?? string.Empty, 62) : 0;
        var replyLines = EstimateWrappedLineCount(_state.CompletionText ?? string.Empty, 62);
        var estimated = 178d + (_state.HasCompletionUserPrompt ? Math.Min(120d, 42d + promptLines * 16d) : 0d) + Math.Min(220d, 42d + replyLines * 16d);
        return Math.Clamp(estimated, Math.Min(MinCompletionCardHeight, maxSurfaceHeight), maxSurfaceHeight);
    }

    private double GetMaxWindowHeight()
    {
        var area = GetCurrentMonitorWorkAreaDip();
        return Math.Max(GetCollapsedHorizontalHeight(), area.Height - WorkAreaMargin);
    }

    private static int EstimateWrappedLineCount(string text, int charsPerLine)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 1;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var count = 0;
        foreach (var line in lines)
            count += Math.Max(1, (int)Math.Ceiling(line.Length / (double)Math.Max(1, charsPerLine)));
        return count;
    }

    private void PositionWindow()
    {
        var layout = CalculateWindowLayout();
        Left = layout.Left;
        Top = layout.Top;
    }

    private (double Left, double Top) CalculateWindowPosition(double width, double height)
    {
        if (IsOrbHudMode())
            return CalculateOrbAnchoredWindowPosition(width, height);

        var position = WpfHudDisplayPosition.Normalize(_settings.Get("display_position", WpfHudDisplayPosition.Default));
        var area = GetCurrentMonitorWorkAreaDip();
        var left = position switch
        {
            WpfHudDisplayPosition.MiddleLeft => area.Left,
            WpfHudDisplayPosition.MiddleRight => area.Right - width,
            _ => area.Left + (area.Width - width) / 2
        };
        var top = position switch
        {
            WpfHudDisplayPosition.BottomCenter => area.Bottom - height,
            WpfHudDisplayPosition.MiddleLeft or WpfHudDisplayPosition.MiddleRight => area.Top + (area.Height - height) / 2,
            _ => area.Top
        };
        return (left, top);
    }

    private (double Left, double Top) CalculateOrbAnchoredWindowPosition(double width, double height)
    {
        var orbWidth = GetCollapsedHorizontalWidth();
        var orbHeight = GetCollapsedHorizontalHeight();
        var (orbLeft, orbTop) = ResolveOrbWindowPosition(orbWidth, orbHeight);

        // Collapsed orb: use the stored/anchor orb rect (or live drag position).
        if (_state.SurfaceKind == WpfHudSurfaceKind.Collapsed && !_pendingLayerExpanded)
            return (orbLeft, orbTop);

        var orbCenterX = orbLeft + orbWidth / 2d;
        var area = GetOrbWorkAreaDip(orbLeft, orbTop, orbWidth, orbHeight);

        var left = orbCenterX - width / 2d;
        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - width));

        // Prefer panel above the orb (orb sits near bottom center of panel); flip below when needed.
        var topAbove = orbTop + orbHeight - height;
        var topBelow = orbTop;
        double top;
        if (topAbove >= area.Top)
            top = topAbove;
        else if (topBelow + height <= area.Bottom)
            top = topBelow;
        else
            top = Math.Clamp(topAbove, area.Top, Math.Max(area.Top, area.Bottom - height));

        return (left, top);
    }

    private (double Left, double Top) ResolveOrbWindowPosition(double width, double height)
    {
        if (_orbDragging && !double.IsNaN(Left) && !double.IsNaN(Top))
        {
            var area = GetOrbWorkAreaDip(Left, Top, width, height);
            return (
                Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - width)),
                Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - height)));
        }

        if (TryReadPersistedOrbPosition(width, height, out var left, out var top))
            return (left, top);

        // Prefer live window position when already shown as orb (avoids snap-back mid-session).
        if (IsVisible
            && _state.SurfaceKind == WpfHudSurfaceKind.Collapsed
            && !double.IsNaN(Left)
            && !double.IsNaN(Top)
            && ActualWidth > 0
            && Math.Abs(ActualWidth - width) < 8d)
        {
            var area = GetOrbWorkAreaDip(Left, Top, width, height);
            return (
                Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - width)),
                Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - height)));
        }

        return CalculateAnchorWindowPosition(width, height);
    }

    private bool TryReadPersistedOrbPosition(double width, double height, out double left, out double top)
    {
        left = 0d;
        top = 0d;
        if (!_settings.Has(WpfHudDensityMode.OrbLeftKey) || !_settings.Has(WpfHudDensityMode.OrbTopKey))
            return false;

        left = _settings.Get(WpfHudDensityMode.OrbLeftKey, double.NaN);
        top = _settings.Get(WpfHudDensityMode.OrbTopKey, double.NaN);
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top))
            return false;

        var area = GetOrbWorkAreaDip(left, top, width, height);
        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - width));
        top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - height));
        return true;
    }

    private (double Left, double Top) CalculateAnchorWindowPosition(double width, double height)
    {
        var position = WpfHudDisplayPosition.Normalize(_settings.Get("display_position", WpfHudDisplayPosition.Default));
        var area = GetCurrentMonitorWorkAreaDip();
        var left = position switch
        {
            WpfHudDisplayPosition.MiddleLeft => area.Left,
            WpfHudDisplayPosition.MiddleRight => area.Right - width,
            _ => area.Left + (area.Width - width) / 2
        };
        var top = position switch
        {
            WpfHudDisplayPosition.BottomCenter => area.Bottom - height,
            WpfHudDisplayPosition.MiddleLeft or WpfHudDisplayPosition.MiddleRight => area.Top + (area.Height - height) / 2,
            _ => area.Top
        };
        return (left, top);
    }

    private Rect GetOrbWorkAreaDip(double left, double top, double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0d ? dpi.DpiScaleX : 1d;
        var scaleY = dpi.DpiScaleY > 0d ? dpi.DpiScaleY : 1d;
        var physical = new Rect(
            left * scaleX,
            top * scaleY,
            Math.Max(1d, width) * scaleX,
            Math.Max(1d, height) * scaleY);
        if (WpfMonitorService.GetMonitorFromPhysicalRect(physical) is { } hit)
            return hit.WorkAreaDip;

        var monitorId = _settings.Get(WpfHudDensityMode.OrbMonitorIdKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(monitorId)
            && WpfMonitorService.ResolveMonitor(monitorId, this) is { } saved
            && !string.Equals(monitorId, WpfMonitorService.AutoMonitorId, StringComparison.OrdinalIgnoreCase))
        {
            // Only trust saved id when it still resolves to that id (not mouse/primary fallback).
            if (string.Equals(saved.Id, monitorId, StringComparison.OrdinalIgnoreCase)
                || saved.Id.StartsWith(monitorId.Split('|')[0] + "|", StringComparison.OrdinalIgnoreCase))
                return saved.WorkAreaDip;
        }

        return GetCurrentMonitorWorkAreaDip();
    }

    private void PersistOrbPosition(double left, double top)
    {
        _settings.Set(WpfHudDensityMode.OrbLeftKey, left);
        _settings.Set(WpfHudDensityMode.OrbTopKey, top);
        var width = GetCurrentWindowWidth() > 0 ? GetCurrentWindowWidth() : OrbCollapsedSize;
        var height = GetCurrentWindowHeight() > 0 ? GetCurrentWindowHeight() : OrbCollapsedSize;
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0d ? dpi.DpiScaleX : 1d;
        var scaleY = dpi.DpiScaleY > 0d ? dpi.DpiScaleY : 1d;
        var physical = new Rect(left * scaleX, top * scaleY, width * scaleX, height * scaleY);
        var monitor = WpfMonitorService.GetMonitorFromPhysicalRect(physical)
            ?? WpfMonitorService.ResolveMonitor(_settings.Get("display_monitor", WpfMonitorService.AutoMonitorId), this)
            ?? WpfMonitorService.GetMonitors().FirstOrDefault();
        if (monitor is not null)
            _settings.Set(WpfHudDensityMode.OrbMonitorIdKey, monitor.Id);
    }

    private Rect GetCurrentMonitorWorkAreaDip()
    {
        if (GetTargetMonitor() is { } monitor)
            return monitor.WorkAreaDip;

        return SystemParameters.WorkArea;
    }

    private WpfMonitorOption? GetTargetMonitor()
    {
        var configuredMonitor = _settings.Get("display_monitor", WpfMonitorService.AutoMonitorId);
        return WpfMonitorService.ResolveMonitor(configuredMonitor, this);
    }

    private DpiScale GetDpiForWindowLayout(IntPtr handle, WindowLayout layout)
    {
        if (GetTargetMonitor() is { } selectedMonitor)
            return selectedMonitor.Dpi;

        var currentDpi = GetCurrentMonitorDpiScale();
        var physicalRect = new Rect(
            layout.Left * currentDpi.DpiScaleX,
            layout.Top * currentDpi.DpiScaleY,
            layout.Width * currentDpi.DpiScaleX,
            layout.Height * currentDpi.DpiScaleY);
        if (WpfMonitorService.GetMonitorFromPhysicalRect(physicalRect) is { } targetMonitor)
            return targetMonitor.Dpi;

        return currentDpi;
    }

    private DpiScale GetCurrentMonitorDpiScale()
    {
        if (GetTargetMonitor() is { } monitor)
            return monitor.Dpi;

        return VisualTreeHelper.GetDpi(this);
    }

    private bool IsSideCenterPosition() =>
        WpfHudDisplayPosition.IsSideCenter(_settings.Get("display_position", WpfHudDisplayPosition.Default));
    private bool IsTopDisplayPosition() =>
        WpfHudDisplayPosition.Normalize(_settings.Get("display_position", WpfHudDisplayPosition.Default)) == WpfHudDisplayPosition.TopCenter;
    private bool UseSideCollapsedLayout() =>
        !IsOrbHudMode() && IsSideCenterPosition() && _state.SurfaceKind == WpfHudSurfaceKind.Collapsed && !_pendingLayerExpanded;

    private bool IsPointerInsideHudWindowBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return IsMouseOver;

        if (!GetCursorPos(out var cursor) || !GetWindowRect(handle, out var windowRect))
            return IsMouseOver;

        return cursor.X >= windowRect.Left
            && cursor.X < windowRect.Right
            && cursor.Y >= windowRect.Top
            && cursor.Y < windowRect.Bottom;
    }

    private void ApplyFullscreenVisibility()
    {
        if (_settings.Get("hide_when_fullscreen", true) != true)
        {
            if (_hiddenForFullscreen)
            {
                _hiddenForFullscreen = false;
                RestoreAfterFullscreen();
            }
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var isFullscreen = WpfFullscreenDetector.IsForegroundFullscreen(WpfFullscreenDetector.GetWindowScreenBounds(this), handle);
        if (isFullscreen)
        {
            if (IsVisible || !_hiddenForFullscreen)
            {
                _hiddenForFullscreen = true;
                Hide();
            }
            return;
        }

        if (_hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            RestoreAfterFullscreen();
        }
    }

    private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_orbDragging)
            return;

        _hoverCloseTimer.Stop();
        _pendingAutoCollapseTimer.Stop();

        if (_state.HasPendingAction)
        {
            if (CanShowFoldablePendingLayer() && !_pendingLayerExpanded)
                StartHoverOpenTimer();
            return;
        }

        if (_state.SurfaceKind == WpfHudSurfaceKind.Collapsed && _state.HasSessions)
            StartHoverOpenTimer();
    }

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_orbDragging)
            return;

        _hoverOpenTimer.Stop();
        if (IsShellTransitionProtected())
            return;

        if (_state.HasPendingAction)
        {
            _hoverCloseTimer.Stop();
            if (CanShowFoldablePendingLayer() && _pendingLayerExpanded)
                RestartPendingAutoCollapse();
            return;
        }

        if (_state.SurfaceKind == WpfHudSurfaceKind.SessionList)
            _hoverCloseTimer.Start();
    }

    private void OnPendingHostMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverOpenTimer.Stop();
        _hoverCloseTimer.Stop();
        _pendingAutoCollapseTimer.Stop();
    }

    private void OnPendingHostMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverOpenTimer.Stop();
        _hoverCloseTimer.Stop();
        if (IsShellTransitionProtected())
            return;

        if (CanShowFoldablePendingLayer() && _pendingLayerExpanded)
            RestartPendingAutoCollapse();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanStartOrbDrag())
            return;

        if (!GetCursorPos(out var cursor))
            return;

        _orbDragging = true;
        _orbDragMoved = false;
        _orbDragStartScreen = new System.Windows.Point(cursor.X, cursor.Y);
        _orbDragOriginLeft = Left;
        _orbDragOriginTop = Top;
        _hoverOpenTimer.Stop();
        CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_orbDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (!GetCursorPos(out var cursor))
            return;

        var dx = cursor.X - _orbDragStartScreen.X;
        var dy = cursor.Y - _orbDragStartScreen.Y;
        if (!_orbDragMoved && Math.Sqrt(dx * dx + dy * dy) < OrbDragThreshold)
            return;

        _orbDragMoved = true;
        _hoverOpenTimer.Stop();

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0d ? dpi.DpiScaleX : 1d;
        var scaleY = dpi.DpiScaleY > 0d ? dpi.DpiScaleY : 1d;
        var width = GetCurrentWindowWidth() > 0 ? GetCurrentWindowWidth() : OrbCollapsedSize;
        var height = GetCurrentWindowHeight() > 0 ? GetCurrentWindowHeight() : OrbCollapsedSize;
        var nextLeft = _orbDragOriginLeft + dx / scaleX;
        var nextTop = _orbDragOriginTop + dy / scaleY;
        var area = GetOrbWorkAreaDip(nextLeft, nextTop, width, height);
        nextLeft = Math.Clamp(nextLeft, area.Left, Math.Max(area.Left, area.Right - width));
        nextTop = Math.Clamp(nextTop, area.Top, Math.Max(area.Top, area.Bottom - height));
        Left = nextLeft;
        Top = nextTop;
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_orbDragging)
            return;

        var moved = _orbDragMoved;
        _orbDragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();

        if (moved)
        {
            PersistOrbPosition(Left, Top);
            e.Handled = true;
            return;
        }

        // Click (no drag): open list or let pending hover path handle it.
        if (_state.SurfaceKind == WpfHudSurfaceKind.Collapsed)
        {
            _hoverOpenTimer.Stop();
            if (!_state.HasPendingAction && _state.HasSessions)
            {
                _state.ShowSessionList();
                e.Handled = true;
            }
            else if (_state.HasPendingAction && CanShowFoldablePendingLayer() && !_pendingLayerExpanded)
            {
                SetPendingLayerExpanded(true);
                e.Handled = true;
            }
        }
    }

    private bool CanStartOrbDrag() =>
        IsOrbHudMode()
        && _state.SurfaceKind == WpfHudSurfaceKind.Collapsed
        && !_pendingLayerExpanded
        && !IsShellTransitionProtected();

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Orb mode handles click/drag in preview handlers.
        if (IsOrbHudMode())
            return;

        if (_state.SurfaceKind != WpfHudSurfaceKind.Collapsed)
            return;

        _hoverOpenTimer.Stop();
        if (_state.HasPendingAction)
            return;

        _state.ShowSessionList();
        e.Handled = true;
    }

    private void StartHoverOpenTimer()
    {
        if (_orbDragging)
            return;

        _hoverOpenTimer.Interval = TimeSpan.FromMilliseconds(_state.HasPendingAction
            ? PendingHoverOpenMilliseconds
            : IsOrbHudMode()
                ? OrbHoverOpenMilliseconds
                : IsCompactHudMode()
                    ? CompactHoverOpenMilliseconds
                    : ClassicHoverOpenMilliseconds);
        _hoverOpenTimer.Start();
    }

    private void ApplyNoActivate()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hoverOpenTimer.Stop();
        _hoverCloseTimer.Stop();
        _pendingAutoCollapseTimer.Stop();
        _transitionGraceTimer.Stop();
        _deferredSessionListShrinkTimer.Stop();
        _fullscreenTimer.Stop();
        ClearWindowLayoutAnimations();
        MouseEnter -= OnMouseEnter;
        MouseLeave -= OnMouseLeave;
        PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        PreviewMouseMove -= OnPreviewMouseMove;
        PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        PendingHost.MouseEnter -= OnPendingHostMouseEnter;
        PendingHost.MouseLeave -= OnPendingHostMouseLeave;
        MouseLeftButtonUp -= OnMouseLeftButtonUp;
        _state.PropertyChanged -= OnStatePropertyChanged;
        _settings.SettingChanged -= OnSettingChanged;
        ReleaseShellTransitionHudVisualUpdateDeferral();
        base.OnClosed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
