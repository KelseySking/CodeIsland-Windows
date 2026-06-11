using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodeIsland.WpfApp.Views;

internal sealed record HudMorphPlan(
    RectangleGeometry? Clip,
    Rect InitialRect,
    Rect? MidRect,
    Rect FinalRect,
    Duration Duration,
    bool UseClip,
    System.Windows.Point TransformOrigin,
    double InitialOpacity = 0.96d,
    double InitialScale = 1d,
    double InitialClipRadius = 0d,
    double? MidClipRadius = null,
    double FinalClipRadius = 0d,
    TimeSpan? MidKeyTime = null,
    Rect? CompletionClipRect = null,
    double? CompletionClipRadius = null,
    bool UseSnapshotLayer = false);

internal sealed class HudMorphAnimator
{
    private readonly FrameworkElement _shell;
    private readonly ScaleTransform _shellScale;
    private readonly TranslateTransform _shellTranslate;
    private readonly System.Windows.Controls.Image _snapshot;
    private readonly ScaleTransform _snapshotScale;
    private readonly TranslateTransform _snapshotTranslate;
    private CacheMode? _previousShellCacheMode;
    private bool _hasPreviousShellCacheMode;

    public HudMorphAnimator(
        FrameworkElement shell,
        ScaleTransform shellScale,
        TranslateTransform shellTranslate,
        System.Windows.Controls.Image snapshot,
        ScaleTransform snapshotScale,
        TranslateTransform snapshotTranslate)
    {
        _shell = shell;
        _shellScale = shellScale;
        _shellTranslate = shellTranslate;
        _snapshot = snapshot;
        _snapshotScale = snapshotScale;
        _snapshotTranslate = snapshotTranslate;
    }

    public void Prepare(HudMorphPlan plan)
    {
        Stop(clearClip: false);
        var useSnapshotLayer = plan.UseSnapshotLayer && TryPrepareSnapshotLayer(plan);
        if (!useSnapshotLayer)
            PrepareShellLayer();

        var target = GetAnimationTarget(useSnapshotLayer);
        var scale = GetAnimationScale(useSnapshotLayer);
        var translate = GetAnimationTranslate(useSnapshotLayer);

        target.Clip = plan.Clip;
        SetClipRadius(plan.Clip, plan.InitialClipRadius);
        target.Opacity = plan.InitialOpacity;
        target.RenderTransformOrigin = plan.TransformOrigin;
        scale.ScaleX = plan.InitialScale;
        scale.ScaleY = plan.InitialScale;
        translate.X = 0d;
        translate.Y = 0d;
    }

    public void Start(HudMorphPlan plan, IEasingFunction easing, Action completed)
    {
        var useSnapshotLayer = plan.UseSnapshotLayer && _snapshot.Visibility == Visibility.Visible && _snapshot.Source is not null;
        var target = GetAnimationTarget(useSnapshotLayer);
        var scale = GetAnimationScale(useSnapshotLayer);
        var translate = GetAnimationTranslate(useSnapshotLayer);

        if (plan.UseClip && plan.Clip is not null)
        {
            var rectAnimation = CreateClipRectAnimation(plan, easing);
            var radiusXAnimation = CreateClipRadiusAnimation(plan, easing);
            var radiusYAnimation = CreateClipRadiusAnimation(plan, easing);
            rectAnimation.Completed += (_, _) =>
            {
                if (ReferenceEquals(target.Clip, plan.Clip))
                {
                    plan.Clip.BeginAnimation(RectangleGeometry.RectProperty, null);
                    plan.Clip.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
                    plan.Clip.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
                    plan.Clip.Rect = plan.FinalRect;
                    SetClipRadius(plan.Clip, plan.FinalClipRadius);
                }

                completed();
                if (useSnapshotLayer)
                    ClearSnapshotLayer();

                ResetAnimationTarget(useSnapshotLayer);

                if (ReferenceEquals(target.Clip, plan.Clip) && plan.CompletionClipRect is { } completionClipRect)
                {
                    plan.Clip.Rect = completionClipRect;
                    SetClipRadius(plan.Clip, plan.CompletionClipRadius ?? plan.FinalClipRadius);
                }

                if (!useSnapshotLayer)
                {
                    _shell.Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(target.Clip, plan.Clip))
                            target.Clip = null;
                        RestoreShellCacheMode();
                    }, DispatcherPriority.ContextIdle);
                }
            };
            plan.Clip.BeginAnimation(RectangleGeometry.RectProperty, rectAnimation);
            plan.Clip.BeginAnimation(RectangleGeometry.RadiusXProperty, radiusXAnimation);
            plan.Clip.BeginAnimation(RectangleGeometry.RadiusYProperty, radiusYAnimation);
        }
        else
        {
            target.Clip = null;
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1d, plan.Duration) { EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1d, plan.Duration) { EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd });
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0d, plan.Duration) { EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0d, plan.Duration) { EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd });

        var opacityAnimation = CreateShellOpacityAnimation(plan, easing);
        if (!plan.UseClip)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                ResetAnimationTarget(useSnapshotLayer);
                completed();
                if (useSnapshotLayer)
                    ClearSnapshotLayer();
                else
                    RestoreShellCacheMode();
            };
        }

        target.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
    }

    public void SwapContent(
        ContentControl host,
        ContentControl outgoingHost,
        object content,
        bool animate,
        Duration duration,
        double slideOffset = 0d)
    {
        host.BeginAnimation(UIElement.OpacityProperty, null);
        outgoingHost.BeginAnimation(UIElement.OpacityProperty, null);
        StopContentMotion(host);
        StopContentMotion(outgoingHost);

        if (!animate)
        {
            outgoingHost.Content = null;
            outgoingHost.Visibility = Visibility.Collapsed;
            host.Content = content;
            host.Opacity = 1d;
            ResetContentMotion(host);
            ResetContentMotion(outgoingHost);
            return;
        }

        var previousContent = host.Content;
        if (previousContent is not null)
        {
            host.Content = null;
            outgoingHost.Content = previousContent;
            outgoingHost.Visibility = Visibility.Visible;
            outgoingHost.Opacity = 1d;

            var fadeOut = new DoubleAnimation(0d, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            var outgoingTranslate = EnsureTranslateTransform(outgoingHost);
            var slideOut = new DoubleAnimation(-slideOffset * 0.45d, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            fadeOut.Completed += (_, _) =>
            {
                outgoingHost.Opacity = 0d;
                outgoingTranslate.Y = 0d;
                outgoingHost.BeginAnimation(UIElement.OpacityProperty, null);
                outgoingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                outgoingHost.Content = null;
                outgoingHost.Visibility = Visibility.Collapsed;
                outgoingHost.Opacity = 1d;
            };
            outgoingHost.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            outgoingTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
        }
        else
        {
            outgoingHost.Content = null;
            outgoingHost.Visibility = Visibility.Collapsed;
        }

        host.Content = content;
        host.Opacity = 0d;
        var incomingTranslate = EnsureTranslateTransform(host);
        incomingTranslate.Y = slideOffset;
        var fadeIn = new DoubleAnimation(1d, duration)
        {
            BeginTime = TimeSpan.FromMilliseconds(45),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        var slideIn = new DoubleAnimation(0d, duration)
        {
            BeginTime = TimeSpan.FromMilliseconds(30),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        fadeIn.Completed += (_, _) =>
        {
            host.Opacity = 1d;
            incomingTranslate.Y = 0d;
            host.BeginAnimation(UIElement.OpacityProperty, null);
            incomingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        };
        host.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        incomingTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    public void FadeIn(FrameworkElement element, Duration duration, double slideOffset = 0d)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        StopContentMotion(element);
        element.Opacity = 0d;
        var translate = EnsureTranslateTransform(element);
        translate.Y = slideOffset;
        var fadeIn = new DoubleAnimation(1d, duration)
        {
            BeginTime = TimeSpan.FromMilliseconds(45),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        var slideIn = new DoubleAnimation(0d, duration)
        {
            BeginTime = TimeSpan.FromMilliseconds(30),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        fadeIn.Completed += (_, _) =>
        {
            element.Opacity = 1d;
            translate.Y = 0d;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
        };
        element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        translate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    public void Stop(bool clearClip = false)
    {
        _shell.BeginAnimation(UIElement.OpacityProperty, null);
        _shellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _shellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _shellTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _shellTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _snapshot.BeginAnimation(UIElement.OpacityProperty, null);
        _snapshotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _snapshotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _snapshotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _snapshotTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        if (_shell.Clip is RectangleGeometry clip)
        {
            clip.BeginAnimation(RectangleGeometry.RectProperty, null);
            clip.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
            clip.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
        }
        if (_snapshot.Clip is RectangleGeometry snapshotClip)
        {
            snapshotClip.BeginAnimation(RectangleGeometry.RectProperty, null);
            snapshotClip.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
            snapshotClip.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
        }
        if (clearClip)
            _shell.Clip = null;
        _snapshot.Clip = null;
        ClearSnapshotLayer();
        RestoreShellCacheMode();
    }

    public void ResetShell()
    {
        _shell.Opacity = 1d;
        _shellScale.ScaleX = 1d;
        _shellScale.ScaleY = 1d;
        _shellTranslate.X = 0d;
        _shellTranslate.Y = 0d;
    }

    private FrameworkElement GetAnimationTarget(bool useSnapshotLayer) =>
        useSnapshotLayer ? _snapshot : _shell;

    private ScaleTransform GetAnimationScale(bool useSnapshotLayer) =>
        useSnapshotLayer ? _snapshotScale : _shellScale;

    private TranslateTransform GetAnimationTranslate(bool useSnapshotLayer) =>
        useSnapshotLayer ? _snapshotTranslate : _shellTranslate;

    private static DoubleAnimation CreateShellOpacityAnimation(HudMorphPlan plan, IEasingFunction easing)
    {
        return new DoubleAnimation(1d, plan.Duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
    }

    private static AnimationTimeline CreateClipRectAnimation(HudMorphPlan plan, IEasingFunction easing)
    {
        if (CanUseMidKeyFrame(plan) && plan.MidRect is { } midRect && plan.MidKeyTime is { } midKeyTime)
        {
            var animation = new RectAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
            animation.KeyFrames.Add(new LinearRectKeyFrame(midRect, KeyTime.FromTimeSpan(midKeyTime)));
            animation.KeyFrames.Add(new LinearRectKeyFrame(plan.FinalRect, KeyTime.FromTimeSpan(plan.Duration.TimeSpan)));
            return animation;
        }

        return new RectAnimation(plan.FinalRect, plan.Duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
    }

    private static AnimationTimeline CreateClipRadiusAnimation(HudMorphPlan plan, IEasingFunction easing)
    {
        if (CanUseMidKeyFrame(plan) && plan.MidClipRadius is { } midRadius && plan.MidKeyTime is { } midKeyTime)
        {
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(midRadius, KeyTime.FromTimeSpan(midKeyTime)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(plan.FinalClipRadius, KeyTime.FromTimeSpan(plan.Duration.TimeSpan)));
            return animation;
        }

        return new DoubleAnimation(plan.FinalClipRadius, plan.Duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
    }

    private static bool CanUseMidKeyFrame(HudMorphPlan plan) =>
        plan.Duration.HasTimeSpan &&
        plan.MidKeyTime is { } midKeyTime &&
        midKeyTime > TimeSpan.Zero &&
        midKeyTime < plan.Duration.TimeSpan;

    private void PrepareShellLayer()
    {
        CaptureShellCacheMode();
        _shell.CacheMode = CreateAnimationCacheMode();
        _shell.Visibility = Visibility.Visible;
        ClearSnapshotLayer();
    }

    private bool TryPrepareSnapshotLayer(HudMorphPlan plan)
    {
        var snapshot = CreateShellSnapshot();
        if (snapshot is null)
            return false;

        _snapshot.Source = snapshot;
        _snapshot.Width = Math.Max(1d, plan.InitialRect.Width);
        _snapshot.Height = Math.Max(1d, plan.InitialRect.Height);
        _snapshot.Visibility = Visibility.Visible;
        _shell.Visibility = Visibility.Hidden;
        RestoreShellCacheMode();
        return true;
    }

    private void ClearSnapshotLayer()
    {
        _shell.Visibility = Visibility.Visible;
        _snapshot.Visibility = Visibility.Collapsed;
        _snapshot.Source = null;
        _snapshot.Opacity = 1d;
        _snapshot.Clip = null;
        _snapshotScale.ScaleX = 1d;
        _snapshotScale.ScaleY = 1d;
        _snapshotTranslate.X = 0d;
        _snapshotTranslate.Y = 0d;
    }

    private void ResetAnimationTarget(bool useSnapshotLayer)
    {
        var target = GetAnimationTarget(useSnapshotLayer);
        var scale = GetAnimationScale(useSnapshotLayer);
        var translate = GetAnimationTranslate(useSnapshotLayer);

        target.Opacity = 1d;
        scale.ScaleX = 1d;
        scale.ScaleY = 1d;
        translate.X = 0d;
        translate.Y = 0d;

        target.BeginAnimation(UIElement.OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void CaptureShellCacheMode()
    {
        if (_hasPreviousShellCacheMode)
            return;

        _previousShellCacheMode = _shell.CacheMode;
        _hasPreviousShellCacheMode = true;
    }

    private void RestoreShellCacheMode()
    {
        if (!_hasPreviousShellCacheMode)
            return;

        _shell.CacheMode = _previousShellCacheMode;
        _previousShellCacheMode = null;
        _hasPreviousShellCacheMode = false;
    }

    private BitmapCache CreateAnimationCacheMode()
    {
        var dpi = VisualTreeHelper.GetDpi(_shell);
        return new BitmapCache
        {
            RenderAtScale = Math.Max(1d, dpi.DpiScaleX),
            SnapsToDevicePixels = true
        };
    }

    private static void StopContentMotion(FrameworkElement element)
    {
        if (TryGetTranslateTransform(element) is not { } translate)
            return;

        translate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private static void ResetContentMotion(FrameworkElement element)
    {
        if (TryGetTranslateTransform(element) is not { } translate)
            return;

        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.Y = 0d;
    }

    private static void SetClipRadius(RectangleGeometry? clip, double radius)
    {
        if (clip is null)
            return;

        clip.RadiusX = radius;
        clip.RadiusY = radius;
    }

    private static TranslateTransform EnsureTranslateTransform(FrameworkElement element)
    {
        if (TryGetTranslateTransform(element) is { } existing)
            return existing;

        var translate = new TranslateTransform();
        if (element.RenderTransform is not null && !ReferenceEquals(element.RenderTransform, Transform.Identity))
        {
            var group = new TransformGroup();
            group.Children.Add(element.RenderTransform);
            group.Children.Add(translate);
            element.RenderTransform = group;
            return translate;
        }

        element.RenderTransform = translate;
        return translate;
    }

    private static TranslateTransform? TryGetTranslateTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform translate)
            return translate;

        if (element.RenderTransform is TransformGroup group)
        {
            foreach (var child in group.Children)
            {
                if (child is TranslateTransform childTranslate)
                    return childTranslate;
            }
        }

        return null;
    }

    private RenderTargetBitmap? CreateShellSnapshot()
    {
        _shell.UpdateLayout();
        var width = _shell.ActualWidth;
        var height = _shell.ActualHeight;
        if (width <= 0d || height <= 0d)
            return null;

        var dpi = VisualTreeHelper.GetDpi(_shell);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96d * dpi.DpiScaleX,
            96d * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(_shell);
        bitmap.Freeze();
        return bitmap;
    }
}
