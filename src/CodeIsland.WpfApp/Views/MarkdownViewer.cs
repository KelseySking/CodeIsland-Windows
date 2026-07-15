using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace CodeIsland.WpfApp.Views;

/// <summary>
/// 绑定 Markdown 字符串，自动按控件宽度换行渲染。
/// </summary>
public sealed class MarkdownViewer : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata(null, OnMarkdownChanged));

    public static readonly DependencyProperty BodyFontSizeProperty = DependencyProperty.Register(
        nameof(BodyFontSize),
        typeof(double),
        typeof(MarkdownViewer),
        new PropertyMetadata(12d, OnMarkdownChanged));

    public MarkdownViewer()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        IsToolBarVisible = false;
        IsSelectionEnabled = true;
        Zoom = 100;
        MinZoom = 100;
        MaxZoom = 100;
        Background = System.Windows.Media.Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Focusable = false;
        SizeChanged += (_, _) => ApplyPageWidth();
    }

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public double BodyFontSize
    {
        get => (double)GetValue(BodyFontSizeProperty);
        set => SetValue(BodyFontSizeProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
            viewer.Rebuild();
    }

    private void Rebuild()
    {
        Document = MarkdownDocumentConverter.ToDocument(Markdown, BodyFontSize);
        ApplyPageWidth();
    }

    private void ApplyPageWidth()
    {
        if (Document is not FlowDocument doc)
            return;

        var width = ActualWidth - Padding.Left - Padding.Right - 4;
        if (width > 1)
            doc.PageWidth = width;
    }
}
