using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodeIsland.Core.Services;
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
    private const int PendingHoverOpenMilliseconds = 280;
    private const int ClassicHoverOpenMilliseconds = 360;
    private const int CompactHoverOpenMilliseconds = 520;
    private const double MinPendingCardHeight = 220d;
    private const double MinCompletionCardHeight = 260d;
    private const double MinHudDetailHeight = 300d;
    private const double CenterSuctionSourceSize = 8d;
    private const double WorkAreaMargin = 40d;
    private const double TransitionSizeTolerance = 1d;
    private const int PendingTransitionMilliseconds = 210;
    private const int PendingAutoCollapseSeconds = 5;
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

    public HudWindow(WpfAppState state, SettingsManager settings)
    {
        InitializeComponent();
        _state = state;
        _settings = settings;
        _morphAnimator = new HudMorphAnimator(Shell, ShellScale, ShellTranslate, ShellSnapshot, SnapshotScale, SnapshotTranslate);
        DataContext = state;

        _hoverOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360) };
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

        _hoverCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
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
        PendingHost.MouseEnter += OnPendingHostMouseEnter;
        PendingHost.MouseLeave += OnPendingHostMouseLeave;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        _state.PropertyChanged += OnStatePropertyChanged;
        _settings.SettingChanged += OnSettingChanged;
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key is "display_position" or "display_monitor" or "hud_density_mode")
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

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            _ => typeof(CollapsedBarView)
        };
        var previousSurfaceType = _currentSurfaceType;
        var surfaceChanged = _currentSurfaceType != next;
        var expectedPendingExpanded = GetExpectedPendingLayerExpanded();
        var pendingExpandedChanged = previousPendingExpanded != expectedPendingExpanded;
        var duration = surfaceChanged ? animationSettings.SurfaceDuration : animationSettings.PendingDuration;
        var useCollapsedSource = ShouldUseCollapsedSource(previousSurfaceType, next);

        if (pendingExpandedChanged)
            _pendingLayerExpanded = expectedPendingExpanded;

        var targetLayout = CalculateWindowLayout();
        var transitionKind = ResolveShellTransitionKind(previousLayout, targetLayout);
        if ((surfaceChanged || pendingExpandedChanged) && transitionKind == ShellTransitionKind.Shrink)
        {
            var deferTargetContentUntilCompleted = useCollapsedSource || pendingExpandedChanged || animationSettings.UsesSnapshotLayerForShrink;
            var revealCollapsedContentAfterShrink = deferTargetContentUntilCompleted && next == typeof(CollapsedBarView);
            if (deferTargetContentUntilCompleted)
                _pendingLayerExpanded = previousPendingExpanded;

            var transitionPlan = PrepareShellTransition(
                previousLayout,
                targetLayout,
                duration,
                transitionKind,
                useCollapsedSource,
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

        if ((surfaceChanged || pendingExpandedChanged) && transitionKind == ShellTransitionKind.Expand)
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
        if (surfaceChanged || pendingExpandedChanged)
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
            if (_state.SurfaceKind != WpfHudSurfaceKind.Collapsed || SurfaceHost.Content is not CollapsedBarView)
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

    private void SetPendingLayerExpanded(bool expanded)
    {
        if (!CanShowFoldablePendingLayer() || _pendingLayerExpanded == expanded)
            return;

        var animationSettings = GetHudAnimationSettings();
        var previousLayout = CaptureCurrentLayout();
        var previousPendingExpanded = _pendingLayerExpanded;
        _pendingLayerExpanded = expanded;
        var targetLayout = CalculateWindowLayout();
        var transitionKind = ResolveShellTransitionKind(previousLayout, targetLayout);
        if (transitionKind == ShellTransitionKind.Shrink)
        {
            _pendingLayerExpanded = previousPendingExpanded;
            var transitionPlan = PrepareShellTransition(
                previousLayout,
                targetLayout,
                animationSettings.PendingDuration,
                transitionKind,
                useCollapsedSource: true,
                completedAction: () =>
                {
                    _pendingLayerExpanded = expanded;
                    RenderPending(animationSettings);
                    UpdateCollapsedBarOrientation();
                    UpdateSurfaceHostPresentation();
                    UpdateWindowBounds();
                    Shell.UpdateLayout();
                });
            StartPreparedShellTransition(transitionPlan);
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

        var (initialRect, midRect, finalRect) = CalculateShellMorphRects(previousLayout, targetLayout, transitionKind, useCollapsedSource);
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
            UseSnapshotLayer: transitionKind == ShellTransitionKind.Shrink && animationSettings.UsesSnapshotLayerForShrink);
        _morphAnimator.Prepare(morphPlan);
        return new ShellTransitionPlan(morphPlan, transitionId, completedAction);
    }

    private static bool ShouldUseCollapsedSource(Type? previousSurfaceType, Type nextSurfaceType) =>
        previousSurfaceType == typeof(CollapsedBarView) || nextSurfaceType == typeof(CollapsedBarView);

    private (Rect InitialRect, Rect? MidRect, Rect FinalRect) CalculateShellMorphRects(
        WindowLayout previousLayout,
        WindowLayout targetLayout,
        ShellTransitionKind transitionKind,
        bool useCollapsedSource)
    {
        if (!useCollapsedSource)
        {
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
        plan.UseClip ? new CriticallyDampedEase() : new CubicEase { EasingMode = EasingMode.EaseOut };

    private sealed class CriticallyDampedEase : EasingFunctionBase
    {
        private const double Response = 8.5d;
        private static readonly double EndValue = Calculate(1d);

        public CriticallyDampedEase()
        {
            EasingMode = EasingMode.EaseIn;
        }

        protected override double EaseInCore(double normalizedTime)
        {
            if (normalizedTime <= 0d)
                return 0d;
            if (normalizedTime >= 1d)
                return 1d;

            return Math.Clamp(Calculate(normalizedTime) / EndValue, 0d, 1d);
        }

        protected override Freezable CreateInstanceCore() => new CriticallyDampedEase();

        private static double Calculate(double time) =>
            1d - (1d + Response * time) * Math.Exp(-Response * time);
    }

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

    private double GetShellPadding() => UseCompactCollapsedChrome() ? CompactCollapsedShellPadding : ShellPadding;

    private bool UseCompactCollapsedChrome() =>
        IsCompactHudMode() && _state.SurfaceKind == WpfHudSurfaceKind.Collapsed && !_pendingLayerExpanded;

    private void ApplyShellChrome()
    {
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

    private CornerRadius GetShellCornerRadius() =>
        UseCompactCollapsedChrome() && UseSideCollapsedLayout() ? new CornerRadius(21d) : new CornerRadius(18d);

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

    private double GetCollapsedHorizontalWidth() => IsCompactHudMode() ? CompactCollapsedHorizontalWidth : CollapsedHorizontalWidth;

    private double GetCollapsedHorizontalHeight() => IsCompactHudMode() ? CompactCollapsedHorizontalHeight : CollapsedHorizontalHeight;

    private double GetCollapsedSideWidth() => IsCompactHudMode() ? CompactCollapsedSideWidth : CollapsedSideWidth;

    private double GetCollapsedSideHeight() => IsCompactHudMode() ? CompactCollapsedSideHeight : CollapsedSideHeight;

    private double GetHudContentWidth() => IsCompactHudMode() ? CompactHudContentWidth : HudContentWidth;

    private bool IsCompactHudMode() =>
        WpfHudDensityMode.IsCompact(_settings.Get("hud_density_mode", WpfHudDensityMode.Default));

    private double CalculateSessionListHeight(double maxSurfaceHeight)
    {
        var itemCount = _state.HudListItems.Count;
        if (itemCount <= 0)
            return Math.Clamp(120d, 104d, maxSurfaceHeight);

        var compactHud = IsCompactHudMode();
        var visibleCardCount = Math.Min(itemCount, compactHud ? 3 : 4);
        var visibleGroupCount = CountVisibleHudListGroups(visibleCardCount);
        var desiredHeight = compactHud
            ? 74d + visibleGroupCount * 30d + visibleCardCount * 72d
            : 88d + visibleGroupCount * 34d + visibleCardCount * 84d;
        var mode = _settings.Get("panel_height_mode", "auto");
        return mode switch
        {
            "fixed" => Math.Clamp(compactHud ? 360d : 420d, compactHud ? 210d : 240d, maxSurfaceHeight),
            "compact" => Math.Clamp(
                compactHud ? 64d + visibleGroupCount * 28d + visibleCardCount * 68d : 74d + visibleGroupCount * 30d + visibleCardCount * 78d,
                compactHud ? 150d : 170d,
                Math.Min(maxSurfaceHeight, compactHud ? 360d : 420d)),
            _ => Math.Clamp(desiredHeight, compactHud ? 160d : 180d, compactHud ? Math.Min(maxSurfaceHeight, 380d) : maxSurfaceHeight)
        };
    }

    private int CountVisibleHudListGroups(int visibleCardCount)
    {
        var remainingCards = visibleCardCount;
        var visibleGroupCount = 0;

        foreach (var group in _state.HudListGroups)
        {
            if (remainingCards <= 0)
                break;

            if (group.Items.Count == 0)
                continue;

            visibleGroupCount++;
            remainingCards -= Math.Min(group.Items.Count, remainingCards);
        }

        return visibleGroupCount;
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
    private bool UseSideCollapsedLayout() => IsSideCenterPosition() && _state.SurfaceKind == WpfHudSurfaceKind.Collapsed && !_pendingLayerExpanded;

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

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
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
        _hoverOpenTimer.Interval = TimeSpan.FromMilliseconds(_state.HasPendingAction
            ? PendingHoverOpenMilliseconds
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
        _fullscreenTimer.Stop();
        MouseEnter -= OnMouseEnter;
        MouseLeave -= OnMouseLeave;
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
