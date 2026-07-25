using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodeIsland.WpfApp.Models;
using CodeIsland.WpfApp.ViewModels;

namespace CodeIsland.WpfApp.Views;

public partial class FloatingOrbView
{
    private Storyboard? _pendingGlowStoryboard;
    private Storyboard? _mascotMotionStoryboard;
    private MotionProfile? _activeMotionProfile;
    private WpfAppState? _observedState;
    private ScaleTransform? _mascotScale;
    private TranslateTransform? _mascotTranslate;

    public FloatingOrbView()
    {
        InitializeComponent();
        AssertMotionProfileMapping();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged += OnAnimationCapabilityChanged;
        RenderCapability.TierChanged += OnAnimationCapabilityChanged;
        AttachState(DataContext as WpfAppState);
        SyncAnimations();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnAnimationCapabilityChanged;
        RenderCapability.TierChanged -= OnAnimationCapabilityChanged;
        StopPendingGlow();
        StopMascotMotion();
        AttachState(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachState(IsLoaded ? e.NewValue as WpfAppState : null);
        SyncAnimations();
    }

    private void AttachState(WpfAppState? state)
    {
        if (ReferenceEquals(_observedState, state))
            return;

        if (_observedState is not null)
            _observedState.PropertyChanged -= OnStatePropertyChanged;

        _observedState = state;
        if (_observedState is not null)
            _observedState.PropertyChanged += OnStatePropertyChanged;
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            SyncAnimations();
            return;
        }

        if (e.PropertyName == nameof(WpfAppState.ActiveStatus))
            SyncMascotMotion();

        if (e.PropertyName is nameof(WpfAppState.HasPendingAction) or nameof(WpfAppState.ShouldShowPendingAlert))
            SyncPendingGlow();
    }

    private void SyncAnimations()
    {
        SyncPendingGlow();
        SyncMascotMotion();
    }

    private void OnAnimationCapabilityChanged(object? sender, EventArgs e) => SyncAnimations();

    private bool CanAnimate =>
        IsLoaded &&
        SystemParameters.ClientAreaAnimation &&
        (RenderCapability.Tier >> 16) >= 2;

    private void SyncPendingGlow()
    {
        var hasPendingAction = DataContext is WpfAppState { HasPendingAction: true };
        if (CanAnimate && hasPendingAction)
            StartPendingGlow();
        else
            StopPendingGlow(hasPendingAction && IsLoaded ? 0.55d : 0d);
    }

    private void StartPendingGlow()
    {
        if (_pendingGlowStoryboard is not null)
            return;

        var duration = TimeSpan.FromSeconds(1d);
        _pendingGlowStoryboard = new Storyboard
        {
            Duration = duration,
            RepeatBehavior = RepeatBehavior.Forever
        };
        AddPulse(_pendingGlowStoryboard, PendingGlow, UIElement.OpacityProperty, 0.25d, 0.85d, duration);
        _pendingGlowStoryboard.Begin(this, HandoffBehavior.SnapshotAndReplace, true);
    }

    private void StopPendingGlow(double opacity = 0d)
    {
        _pendingGlowStoryboard?.Remove(this);
        _pendingGlowStoryboard = null;
        PendingGlow.BeginAnimation(UIElement.OpacityProperty, null);
        PendingGlow.Opacity = opacity;
    }

    private void SyncMascotMotion()
    {
        if (!CanAnimate)
        {
            StopMascotMotion();
            return;
        }

        var status = (DataContext as WpfAppState)?.ActiveStatus ?? AgentStatus.Idle;
        var profile = ResolveMotionProfile(status);
        if (_mascotMotionStoryboard is not null && _activeMotionProfile == profile)
            return;

        StopMascotMotion();
        EnsureWritableTransforms();

        var duration = profile == MotionProfile.Idle
            ? TimeSpan.FromSeconds(3d)
            : profile == MotionProfile.Working
                ? TimeSpan.FromSeconds(0.85d)
                : TimeSpan.FromSeconds(1.1d);
        var scalePeak = profile switch
        {
            MotionProfile.Working => 1.04d,
            MotionProfile.Waiting => 1.01d,
            _ => 1.02d
        };

        _mascotMotionStoryboard = new Storyboard
        {
            Duration = duration,
            RepeatBehavior = RepeatBehavior.Forever
        };
        AddPulse(_mascotMotionStoryboard, _mascotScale!, ScaleTransform.ScaleXProperty, 1d, scalePeak, duration);
        AddPulse(_mascotMotionStoryboard, _mascotScale!, ScaleTransform.ScaleYProperty, 1d, scalePeak, duration);

        if (profile == MotionProfile.Working)
            AddPulse(_mascotMotionStoryboard, _mascotTranslate!, TranslateTransform.YProperty, 0d, -3d, duration);
        else if (profile == MotionProfile.Waiting)
            AddPulse(_mascotMotionStoryboard, MascotHost, UIElement.OpacityProperty, 1d, 0.65d, duration);

        _activeMotionProfile = profile;
        _mascotMotionStoryboard.Begin(this, HandoffBehavior.SnapshotAndReplace, true);
    }

    private void StopMascotMotion()
    {
        _mascotMotionStoryboard?.Remove(this);
        _mascotMotionStoryboard = null;
        _activeMotionProfile = null;

        _mascotScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _mascotScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _mascotTranslate?.BeginAnimation(TranslateTransform.XProperty, null);
        _mascotTranslate?.BeginAnimation(TranslateTransform.YProperty, null);
        MascotHost.BeginAnimation(UIElement.OpacityProperty, null);

        if (_mascotScale is not null)
        {
            _mascotScale.ScaleX = 1d;
            _mascotScale.ScaleY = 1d;
        }

        if (_mascotTranslate is not null)
        {
            _mascotTranslate.X = 0d;
            _mascotTranslate.Y = 0d;
        }

        MascotHost.Opacity = 1d;
    }

    private void EnsureWritableTransforms()
    {
        var group = MascotHost.RenderTransform as TransformGroup;
        if (group is not null && !group.IsFrozen &&
            group.Children.Count == 2 &&
            group.Children[0] is ScaleTransform { IsFrozen: false } scale &&
            group.Children[1] is TranslateTransform { IsFrozen: false } translate)
        {
            _mascotScale = scale;
            _mascotTranslate = translate;
            return;
        }

        _mascotScale = new ScaleTransform(1d, 1d);
        _mascotTranslate = new TranslateTransform();
        group = new TransformGroup();
        group.Children.Add(_mascotScale);
        group.Children.Add(_mascotTranslate);
        MascotHost.RenderTransform = group;
    }

    private static void AddPulse(
        Storyboard storyboard,
        DependencyObject target,
        DependencyProperty property,
        double start,
        double peak,
        TimeSpan duration)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(duration / 2d))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(start, KeyTime.FromTimeSpan(duration))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }

    private static MotionProfile ResolveMotionProfile(AgentStatus status) => status switch
    {
        AgentStatus.Processing or AgentStatus.Running => MotionProfile.Working,
        AgentStatus.WaitingApproval or AgentStatus.WaitingQuestion => MotionProfile.Waiting,
        _ => MotionProfile.Idle
    };

    [Conditional("DEBUG")]
    private static void AssertMotionProfileMapping()
    {
        Debug.Assert(ResolveMotionProfile(AgentStatus.Idle) == MotionProfile.Idle);
        Debug.Assert(ResolveMotionProfile(AgentStatus.Processing) == MotionProfile.Working);
        Debug.Assert(ResolveMotionProfile(AgentStatus.Running) == MotionProfile.Working);
        Debug.Assert(ResolveMotionProfile(AgentStatus.WaitingApproval) == MotionProfile.Waiting);
        Debug.Assert(ResolveMotionProfile(AgentStatus.WaitingQuestion) == MotionProfile.Waiting);
        Debug.Assert(ResolveMotionProfile(AgentStatus.Completed) == MotionProfile.Idle);
    }

    private enum MotionProfile
    {
        Idle,
        Working,
        Waiting
    }
}
