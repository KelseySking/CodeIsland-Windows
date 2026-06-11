using CodeIsland.WpfApp.ViewModels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfButton = System.Windows.Controls.Button;

namespace CodeIsland.WpfApp.Views;

public partial class SessionListView
{
    private readonly HashSet<FrameworkElement> _removingItemRoots = [];

    public SessionListView()
    {
        InitializeComponent();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not WpfButton button ||
            DataContext is not WpfAppState state ||
            FindItemRoot(button) is not { } itemRoot)
        {
            return;
        }

        var parameter = button.CommandParameter;
        var command = state.RemoveHudListItemCommand;
        if (!command.CanExecute(parameter) || !_removingItemRoots.Add(itemRoot))
            return;

        itemRoot.IsHitTestVisible = false;
        var actualHeight = itemRoot.ActualHeight;
        if (actualHeight <= 0d)
        {
            _removingItemRoots.Remove(itemRoot);
            command.Execute(parameter);
            return;
        }

        var startMargin = itemRoot.Margin;
        itemRoot.BeginAnimation(HeightProperty, null);
        itemRoot.Height = actualHeight;
        itemRoot.ClipToBounds = true;
        itemRoot.Opacity = 1d;

        var translate = EnsureWritableTranslateTransform(itemRoot);

        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.Y = 0d;

        var storyboard = new Storyboard { FillBehavior = FillBehavior.HoldEnd };
        storyboard.Children.Add(CreateDoubleAnimation(itemRoot, OpacityProperty, 0d, HudAnimationTimings.ContentDuration, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        storyboard.Children.Add(CreateDoubleAnimation(itemRoot, HeightProperty, 0d, HudAnimationTimings.SurfaceDuration, new HudShellMorphEase()));
        storyboard.Children.Add(CreateThicknessAnimation(itemRoot, startMargin, new Thickness(0d), HudAnimationTimings.SurfaceDuration, new HudShellMorphEase()));
        storyboard.Children.Add(CreateDoubleAnimation(translate, TranslateTransform.YProperty, HudAnimationTimings.ListItemExitSlideOffset, HudAnimationTimings.SurfaceDuration, new HudShellMorphEase()));
        storyboard.Completed += (_, _) =>
        {
            _removingItemRoots.Remove(itemRoot);
            if (command.CanExecute(parameter))
                command.Execute(parameter);
            else
                ResetRemovedItemAnimation(itemRoot, startMargin, translate);
        };
        storyboard.Begin();
    }

    private static FrameworkElement? FindItemRoot(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { Name: "ItemRoot" } element)
                return element;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static DoubleAnimation CreateDoubleAnimation(DependencyObject target, DependencyProperty property, double to, Duration duration, IEasingFunction easingFunction)
    {
        var animation = new DoubleAnimation(to, duration)
        {
            EasingFunction = easingFunction,
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        return animation;
    }

    private static ThicknessAnimation CreateThicknessAnimation(DependencyObject target, Thickness from, Thickness to, Duration duration, IEasingFunction easingFunction)
    {
        var animation = new ThicknessAnimation(from, to, duration)
        {
            EasingFunction = easingFunction,
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(MarginProperty));
        return animation;
    }

    private static TranslateTransform EnsureWritableTranslateTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } existing)
            return existing;

        var translate = new TranslateTransform();
        element.RenderTransform = translate;
        return translate;
    }

    private static void ResetRemovedItemAnimation(FrameworkElement itemRoot, Thickness margin, TranslateTransform translate)
    {
        itemRoot.BeginAnimation(OpacityProperty, null);
        itemRoot.BeginAnimation(HeightProperty, null);
        itemRoot.BeginAnimation(MarginProperty, null);
        if (!translate.IsFrozen)
            translate.BeginAnimation(TranslateTransform.YProperty, null);
        itemRoot.Height = double.NaN;
        itemRoot.Margin = margin;
        itemRoot.Opacity = 1d;
        if (!translate.IsFrozen)
            translate.Y = 0d;
        itemRoot.IsHitTestVisible = true;
    }
}
