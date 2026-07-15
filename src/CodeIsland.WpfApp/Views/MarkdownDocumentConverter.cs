using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBrush = System.Windows.Media.Brush;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace CodeIsland.WpfApp.Views;

/// <summary>
/// Markdown string → FlowDocument，适配 HUD 深色半透明表面。
/// ConverterParameter 可传字号（默认 12）。
/// </summary>
public sealed class MarkdownDocumentConverter : IValueConverter
{
    public static MarkdownDocumentConverter Instance { get; } = new();

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UsePipeTables()
        .UseTaskLists()
        .Build();

    private static readonly WpfBrush BodyBrush = BrushFrom("#F2FFFFFF");
    private static readonly WpfBrush MutedBrush = BrushFrom("#B3FFFFFF");
    private static readonly WpfBrush CodeBrush = BrushFrom("#FFE8C4A8");
    private static readonly WpfBrush CodeBgBrush = BrushFrom("#22000000");
    private static readonly WpfBrush QuoteBarBrush = BrushFrom("#40FFFFFF");
    private static readonly WpfBrush LinkBrush = BrushFrom("#FF8EC8FF");
    private static readonly WpfFontFamily BodyFont = new("Segoe UI");
    private static readonly WpfFontFamily MonoFont = new("Consolas");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fontSize = 12d;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            fontSize = parsed;
        else if (parameter is double d)
            fontSize = d;

        return ToDocument(value as string, fontSize);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;

    public static FlowDocument ToDocument(string? markdown, double fontSize = 12)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = BodyBrush,
            FontFamily = BodyFont,
            FontSize = fontSize,
            TextAlignment = TextAlignment.Left,
            LineHeight = fontSize * 1.35
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.Blocks.Add(CreatePlainParagraph(string.Empty, fontSize));
            return doc;
        }

        try
        {
            var root = Markdown.Parse(markdown, Pipeline);
            RenderBlocks(doc.Blocks, root, fontSize);
            if (doc.Blocks.Count == 0)
                doc.Blocks.Add(CreatePlainParagraph(markdown, fontSize));
        }
        catch
        {
            // ponytail: 解析失败直接纯文本，不抛到 UI
            doc.Blocks.Clear();
            doc.Blocks.Add(CreatePlainParagraph(markdown, fontSize));
        }

        return doc;
    }

    private static void RenderBlocks(BlockCollection blocks, IEnumerable<MdBlock> source, double fontSize)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    blocks.Add(CreateHeading(heading, fontSize));
                    break;
                case ParagraphBlock paragraph:
                    blocks.Add(CreateParagraph(paragraph, fontSize));
                    break;
                case FencedCodeBlock fenced:
                    blocks.Add(CreateCodeSection(fenced.Lines.ToString(), fontSize));
                    break;
                case CodeBlock code:
                    blocks.Add(CreateCodeSection(code.Lines.ToString(), fontSize));
                    break;
                case QuoteBlock quote:
                    blocks.Add(CreateQuote(quote, fontSize));
                    break;
                case ListBlock list:
                    blocks.Add(CreateList(list, fontSize));
                    break;
                case ThematicBreakBlock:
                    blocks.Add(new BlockUIContainer(new System.Windows.Controls.Border
                    {
                        Height = 1,
                        Background = QuoteBarBrush,
                        Margin = new Thickness(0, 6, 0, 6)
                    }));
                    break;
                case MdTable table:
                    blocks.Add(CreateTableFallback(table, fontSize));
                    break;
                case LeafBlock leaf when leaf.Inline != null:
                    blocks.Add(CreateParagraphFromInlines(leaf.Inline, fontSize));
                    break;
            }
        }
    }

    private static Paragraph CreateHeading(HeadingBlock heading, double fontSize)
    {
        var size = fontSize + Math.Max(0, 5 - heading.Level);
        var p = CreateParagraphFromInlines(heading.Inline, size);
        p.FontWeight = FontWeights.SemiBold;
        p.Margin = new Thickness(0, heading.Level <= 2 ? 8 : 4, 0, 4);
        return p;
    }

    private static Paragraph CreateParagraph(ParagraphBlock block, double fontSize) =>
        CreateParagraphFromInlines(block.Inline, fontSize);

    private static Paragraph CreatePlainParagraph(string text, double fontSize)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 4),
            FontSize = fontSize,
            Foreground = BodyBrush
        };
        if (!string.IsNullOrEmpty(text))
            p.Inlines.Add(new Run(text));
        return p;
    }

    private static Paragraph CreateParagraphFromInlines(MdInline? inline, double fontSize)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 4),
            FontSize = fontSize,
            Foreground = BodyBrush
        };
        if (inline != null)
            AppendInlines(p.Inlines, inline, fontSize);
        return p;
    }

    private static Section CreateCodeSection(string text, double fontSize)
    {
        text = text.TrimEnd('\r', '\n');
        var p = new Paragraph
        {
            Margin = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            FontFamily = MonoFont,
            FontSize = Math.Max(10, fontSize - 1),
            Foreground = CodeBrush,
            Background = CodeBgBrush,
            LineHeight = fontSize * 1.3
        };
        p.Inlines.Add(new Run(text));

        return new Section
        {
            Margin = new Thickness(0, 4, 0, 6),
            BorderBrush = QuoteBarBrush,
            BorderThickness = new Thickness(1),
            Blocks = { p }
        };
    }

    private static Section CreateQuote(QuoteBlock quote, double fontSize)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 2, 0, 6),
            BorderBrush = QuoteBarBrush,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Foreground = MutedBrush
        };
        RenderBlocks(section.Blocks, quote, fontSize);
        return section;
    }

    private static List CreateList(ListBlock list, double fontSize)
    {
        var wpfList = new List
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 2, 0, 4),
            Padding = new Thickness(18, 0, 0, 0),
            Foreground = BodyBrush
        };

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var listItem = new ListItem();
            RenderBlocks(listItem.Blocks, item, fontSize);
            if (listItem.Blocks.Count == 0)
                listItem.Blocks.Add(CreatePlainParagraph(string.Empty, fontSize));
            wpfList.ListItems.Add(listItem);
        }

        return wpfList;
    }

    private static Paragraph CreateTableFallback(MdTable table, double fontSize)
    {
        // ponytail: 表格降级为 mono 文本行，避免复杂 Table 布局撑爆 HUD
        var sb = new StringBuilder();
        foreach (var row in table.OfType<MdTableRow>())
        {
            var cells = row.OfType<MdTableCell>().Select(ExtractCellText);
            sb.AppendLine(string.Join(" | ", cells));
        }

        var p = CreatePlainParagraph(sb.ToString().TrimEnd(), Math.Max(10, fontSize - 1));
        p.FontFamily = MonoFont;
        p.Foreground = CodeBrush;
        p.Background = CodeBgBrush;
        p.Padding = new Thickness(8, 6, 8, 6);
        return p;
    }

    private static string ExtractCellText(MdTableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var literal in cell.Descendants<LiteralInline>())
            sb.Append(literal.Content.ToString());
        return sb.ToString().Trim();
    }

    private static void AppendInlines(InlineCollection target, MdInline? inline, double fontSize)
    {
        for (var current = inline; current != null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = MonoFont,
                        Foreground = CodeBrush,
                        Background = CodeBgBrush,
                        FontSize = Math.Max(10, fontSize - 1)
                    });
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2)
                        span.FontWeight = FontWeights.SemiBold;
                    else
                        span.FontStyle = FontStyles.Italic;
                    AppendInlines(span.Inlines, emphasis.FirstChild, fontSize);
                    target.Add(span);
                    break;
                case LinkInline link:
                    var linkSpan = new Span { Foreground = LinkBrush };
                    if (link.FirstChild != null)
                        AppendInlines(linkSpan.Inlines, link.FirstChild, fontSize);
                    else
                        linkSpan.Inlines.Add(new Run(link.Url ?? string.Empty));
                    // 不自动导航：HUD 内点链不安全且无浏览器上下文
                    target.Add(linkSpan);
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case ContainerInline container:
                    AppendInlines(target, container.FirstChild, fontSize);
                    break;
            }
        }
    }

    private static WpfBrush BrushFrom(string hex)
    {
        var brush = (WpfBrush)new BrushConverter().ConvertFromString(hex)!;
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }
}
