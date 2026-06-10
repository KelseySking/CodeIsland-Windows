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
    Rect FinalRect,
    Duration Duration,
    bool UseClip,
    System.Windows.Point TransformOrigin,
    double InitialOpacity = 0.96d,
    double InitialScale = 1d,
    Rect? CompletionClipRect = null,
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
            var rectAnimation = new RectAnimation(plan.FinalRect, plan.Duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            };
            rectAnimation.Completed += (_, _) =>
            {
                if (ReferenceEquals(target.Clip, plan.Clip))
                {
                    plan.Clip.BeginAnimation(RectangleGeometry.RectProperty, null);
                    plan.Clip.Rect = plan.FinalRect;
                }

                completed();
                ResetAnimationTarget(useSnapshotLayer);

                if (ReferenceEquals(target.Clip, plan.Clip) && plan.CompletionClipRect is { } completionClipRect)
                    plan.Clip.Rect = completionClipRect;

                if (useSnapshotLayer)
                {
                    ClearSnapshotLayer();
                }
                else
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
        }
        else
        {
            target.Clip = null;
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1d, plan.Duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1d, plan.Duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0d, plan.Duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0d, plan.Duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });

        var opacityAnimation = new DoubleAnimation(1d, plan.Duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
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
        Duration duration)
    {
        host.BeginAnimation(UIElement.OpacityProperty, null);
        outgoingHost.BeginAnimation(UIElement.OpacityProperty, null);

        if (!animate)
        {
            outgoingHost.Content = null;
            outgoingHost.Visibility = Visibility.Collapsed;
            host.Content = content;
            host.Opacity = 1d;
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
                FillBehavior = FillBehavior.Stop
            };
            fadeOut.Completed += (_, _) =>
            {
                outgoingHost.Content = null;
                outgoingHost.Visibility = Visibility.Collapsed;
                outgoingHost.Opacity = 1d;
            };
            outgoingHost.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
        else
        {
            outgoingHost.Content = null;
            outgoingHost.Visibility = Visibility.Collapsed;
        }

        host.Content = content;
        host.Opacity = 0d;
        var fadeIn = new DoubleAnimation(1d, duration)
        {
            BeginTime = TimeSpan.FromMilliseconds(70),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fadeIn.Completed += (_, _) => host.Opacity = 1d;
        host.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    public void FadeIn(UIElement element, Duration duration)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0d;
        var fadeIn = new DoubleAnimation(1d, duration)
        {
            BeginTime = TimeSpan.FromMilliseconds(70),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fadeIn.Completed += (_, _) => element.Opacity = 1d;
        element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
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
            clip.BeginAnimation(RectangleGeometry.RectProperty, null);
        if (_snapshot.Clip is RectangleGeometry snapshotClip)
            snapshotClip.BeginAnimation(RectangleGeometry.RectProperty, null);
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
        _snapshot.Source = null;
        _snapshot.Visibility = Visibility.Collapsed;
        _snapshot.Opacity = 1d;
        _snapshot.Clip = null;
        _snapshotScale.ScaleX = 1d;
        _snapshotScale.ScaleY = 1d;
        _snapshotTranslate.X = 0d;
        _snapshotTranslate.Y = 0d;
        _shell.Visibility = Visibility.Visible;
    }

    private void ResetAnimationTarget(bool useSnapshotLayer)
    {
        if (!useSnapshotLayer)
        {
            ResetShell();
            return;
        }

        _snapshot.Opacity = 1d;
        _snapshotScale.ScaleX = 1d;
        _snapshotScale.ScaleY = 1d;
        _snapshotTranslate.X = 0d;
        _snapshotTranslate.Y = 0d;
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
