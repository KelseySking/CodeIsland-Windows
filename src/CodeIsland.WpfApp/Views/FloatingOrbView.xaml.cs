using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using CodeIsland.WpfApp.ViewModels;

namespace CodeIsland.WpfApp.Views;

public partial class FloatingOrbView
{
    private Storyboard? _pendingGlowStoryboard;

    public FloatingOrbView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncPendingGlow();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopPendingGlow();
        if (DataContext is WpfAppState state)
            state.PropertyChanged -= OnStatePropertyChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is WpfAppState oldState)
            oldState.PropertyChanged -= OnStatePropertyChanged;
        if (e.NewValue is WpfAppState newState)
            newState.PropertyChanged += OnStatePropertyChanged;
        SyncPendingGlow();
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WpfAppState.HasPendingAction) or nameof(WpfAppState.ShouldShowPendingAlert) or null)
            SyncPendingGlow();
    }

    private void SyncPendingGlow()
    {
        if (DataContext is WpfAppState { HasPendingAction: true })
            StartPendingGlow();
        else
            StopPendingGlow();
    }

    private void StartPendingGlow()
    {
        if (_pendingGlowStoryboard is not null)
            return;

        PendingGlow.Opacity = 0.35d;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.6d),
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.25d, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.85d, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8d)))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.25d, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6d)))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });

        _pendingGlowStoryboard = new Storyboard();
        Storyboard.SetTarget(animation, PendingGlow);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        _pendingGlowStoryboard.Children.Add(animation);
        _pendingGlowStoryboard.Begin();
    }

    private void StopPendingGlow()
    {
        _pendingGlowStoryboard?.Stop();
        _pendingGlowStoryboard = null;
        PendingGlow.BeginAnimation(UIElement.OpacityProperty, null);
        PendingGlow.Opacity = 0d;
    }
}
