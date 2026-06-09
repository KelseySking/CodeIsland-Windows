using System.Windows;

namespace CodeIsland.WpfApp.Views;

public partial class CollapsedBarView
{
    public static readonly DependencyProperty IsVerticalProperty = DependencyProperty.Register(
        nameof(IsVertical),
        typeof(bool),
        typeof(CollapsedBarView),
        new PropertyMetadata(false));

    public bool IsVertical
    {
        get => (bool)GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    public CollapsedBarView()
    {
        InitializeComponent();
    }
}
