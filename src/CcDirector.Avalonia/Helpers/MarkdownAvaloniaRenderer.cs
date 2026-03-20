using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Utilities;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

namespace CcDirector.Avalonia.Helpers;

/// <summary>
/// Converts a Markdown string into an Avalonia control tree styled for the dark theme.
/// Port of WPF MarkdownFlowDocumentRenderer.
/// </summary>
public static class MarkdownAvaloniaRenderer
{
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)).ToImmutable();
    private static readonly IBrush H1Brush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)).ToImmutable();
    private static readonly IBrush H2Brush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)).ToImmutable();
    private static readonly IBrush H3Brush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)).ToImmutable();
    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)).ToImmutable();
    private static readonly IBrush CodeForeground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)).ToImmutable();
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)).ToImmutable();
    private static readonly IBrush QuoteBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)).ToImmutable();
    private static readonly IBrush QuoteBorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)).ToImmutable();
    private static readonly IBrush TableHeaderForeground = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)).ToImmutable();
    private static readonly IBrush TableAltRowBackground = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)).ToImmutable();
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");
    private static readonly FontFamily UiFont = new("Segoe UI");

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static Control Render(string markdown) => Render(markdown, embedded: false);

    public static Control Render(string markdown, bool embedded)
    {
        FileLog.Write("[MarkdownAvaloniaRenderer] Render: begin");

        var container = new StackPanel
        {
            Margin = embedded ? new Thickness(0) : new Thickness(24, 16, 24, 16)
        };

        if (string.IsNullOrEmpty(markdown))
        {
            FileLog.Write("[MarkdownAvaloniaRenderer] Render: empty content");
            return container;
        }

        var parsed = Markdown.Parse(markdown, Pipeline);

        foreach (var block in parsed)
        {
            var element = RenderBlock(block);
            if (element != null)
                container.Children.Add(element);
        }

        FileLog.Write($"[MarkdownAvaloniaRenderer] Render: complete, {container.Children.Count} blocks");
        return container;
    }

    private static Control? RenderBlock(MarkdownObject block)
    {
        return block switch
        {
            HeadingBlock heading => RenderHeading(heading),
            ParagraphBlock paragraph => RenderParagraph(paragraph),
            FencedCodeBlock fencedCode => RenderCodeBlock(fencedCode),
            CodeBlock codeBlock => RenderCodeBlock(codeBlock),
            ListBlock list => RenderList(list),
            QuoteBlock quote => RenderQuote(quote),
            MdTable table => RenderTable(table),
            ThematicBreakBlock => RenderThematicBreak(),
            _ => RenderFallbackBlock(block)
        };
    }

    private static TextBlock RenderHeading(HeadingBlock heading)
    {
        var tb = new TextBlock
        {
            FontFamily = UiFont,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 6)
        };

        switch (heading.Level)
        {
            case 1:
                tb.FontSize = 24;
                tb.Foreground = H1Brush;
                tb.Margin = new Thickness(0, 16, 0, 8);
                break;
            case 2:
                tb.FontSize = 20;
                tb.Foreground = H2Brush;
                tb.Margin = new Thickness(0, 14, 0, 6);
                break;
            case 3:
                tb.FontSize = 16;
                tb.Foreground = H3Brush;
                break;
            case 4:
                tb.FontSize = 14;
                tb.Foreground = H3Brush;
                break;
            default:
                tb.FontSize = 13;
                tb.Foreground = H3Brush;
                break;
        }

        if (heading.Inline != null)
            AddInlines(tb.Inlines!, heading.Inline);

        return tb;
    }

    private static TextBlock RenderParagraph(ParagraphBlock paragraph)
    {
        var tb = new TextBlock
        {
            FontFamily = UiFont,
            FontSize = 14,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };

        if (paragraph.Inline != null)
            AddInlines(tb.Inlines!, paragraph.Inline);

        return tb;
    }

    private static Border RenderCodeBlock(CodeBlock codeBlock)
    {
        var text = ExtractCodeBlockText(codeBlock);

        var tb = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 13,
            Foreground = CodeForeground,
            TextWrapping = TextWrapping.Wrap,
            Text = text
        };

        return new Border
        {
            Background = CodeBackground,
            BorderBrush = QuoteBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 8),
            Child = tb
        };
    }

    private static StackPanel RenderList(ListBlock listBlock)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 4, 0, 8)
        };

        var itemIndex = 1;
        foreach (var item in listBlock)
        {
            if (item is not ListItemBlock listItem)
                continue;

            var row = new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                Margin = new Thickness(24, 0, 0, 2)
            };

            var bullet = listBlock.IsOrdered
                ? $"{itemIndex}. "
                : "- ";

            row.Children.Add(new TextBlock
            {
                Text = bullet,
                FontFamily = UiFont,
                FontSize = 14,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 4, 0)
            });

            var contentPanel = new StackPanel();
            foreach (var child in listItem)
            {
                var rendered = RenderBlock(child);
                if (rendered != null)
                    contentPanel.Children.Add(rendered);
            }

            row.Children.Add(contentPanel);
            panel.Children.Add(row);
            itemIndex++;
        }

        return panel;
    }

    private static Border RenderQuote(QuoteBlock quote)
    {
        var inner = new StackPanel();

        foreach (var child in quote)
        {
            var rendered = RenderBlock(child);
            if (rendered != null)
            {
                // Apply quote color to text blocks
                if (rendered is TextBlock tb)
                    tb.Foreground = QuoteBrush;
                inner.Children.Add(rendered);
            }
        }

        return new Border
        {
            BorderBrush = QuoteBorderBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 0, 4),
            Margin = new Thickness(0, 4, 0, 8),
            Child = inner
        };
    }

    private static Grid RenderTable(MdTable table)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 4, 0, 8)
        };

        // Determine column count from first row
        var columnCount = 0;
        foreach (var child in table)
        {
            if (child is MdTableRow firstRow)
            {
                columnCount = firstRow.Count;
                break;
            }
        }

        for (var i = 0; i < columnCount; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        var rowIndex = 0;
        var dataRowIndex = 0;

        foreach (var child in table)
        {
            if (child is not MdTableRow mdRow)
                continue;

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cellIndex = 0;
            foreach (var cellChild in mdRow)
            {
                if (cellChild is not MdTableCell mdCell)
                    continue;

                var cellContent = new TextBlock
                {
                    FontFamily = UiFont,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap
                };

                // Extract inline content from cell paragraphs
                foreach (var block in mdCell)
                {
                    if (block is ParagraphBlock para && para.Inline != null)
                        AddInlines(cellContent.Inlines!, para.Inline);
                }

                if (mdRow.IsHeader)
                {
                    cellContent.Foreground = TableHeaderForeground;
                    cellContent.FontWeight = FontWeight.SemiBold;
                }
                else
                {
                    cellContent.Foreground = TextBrush;
                }

                var cellBorder = new Border
                {
                    BorderBrush = QuoteBorderBrush,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = cellContent
                };

                if (!mdRow.IsHeader && dataRowIndex % 2 == 1)
                    cellBorder.Background = TableAltRowBackground;

                Grid.SetRow(cellBorder, rowIndex);
                Grid.SetColumn(cellBorder, cellIndex);
                grid.Children.Add(cellBorder);

                cellIndex++;
            }

            if (!mdRow.IsHeader)
                dataRowIndex++;
            rowIndex++;
        }

        return grid;
    }

    private static Border RenderThematicBreak()
    {
        return new Border
        {
            Height = 1,
            Background = QuoteBorderBrush,
            Margin = new Thickness(0, 8, 0, 8)
        };
    }

    private static Control? RenderFallbackBlock(MarkdownObject block)
    {
        if (block is LeafBlock leaf && leaf.Inline != null)
        {
            var tb = new TextBlock
            {
                FontFamily = UiFont,
                FontSize = 14,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            AddInlines(tb.Inlines!, leaf.Inline);
            return tb;
        }

        if (block is ContainerBlock container)
        {
            var panel = new StackPanel();
            foreach (var child in container)
            {
                var rendered = RenderBlock(child);
                if (rendered != null)
                    panel.Children.Add(rendered);
            }
            return panel.Children.Count > 0 ? panel : null;
        }

        return null;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2)
                        span.FontWeight = FontWeight.Bold;
                    else
                        span.FontStyle = FontStyle.Italic;
                    AddInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;

                case CodeInline code:
                    var codeRun = new Run(code.Content)
                    {
                        FontFamily = MonoFont,
                        Background = CodeBackground,
                        Foreground = CodeForeground
                    };
                    target.Add(codeRun);
                    break;

                case LinkInline link:
                    if (link.IsImage)
                    {
                        var altText = link.FirstChild is LiteralInline altLiteral
                            ? altLiteral.Content.ToString()
                            : "[Image]";
                        target.Add(new Run($"[Image: {altText}]") { Foreground = QuoteBrush });
                    }
                    else
                    {
                        var hyperlink = new Span
                        {
                            Foreground = LinkBrush,
                            TextDecorations = TextDecorations.Underline
                        };
                        AddInlines(hyperlink.Inlines, link);
                        target.Add(hyperlink);
                    }
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case HtmlInline html:
                    target.Add(new Run(html.Tag) { Foreground = QuoteBrush, FontSize = 12 });
                    break;

                default:
                    if (inline is ContainerInline nestedContainer)
                        AddInlines(target, nestedContainer);
                    break;
            }
        }
    }

    private static string ExtractCodeBlockText(CodeBlock codeBlock)
    {
        var lines = codeBlock.Lines;
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                builder.AppendLine();
            builder.Append(lines.Lines[i].Slice.ToString());
        }
        return builder.ToString().TrimEnd();
    }
}
