using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using WpfBlock = System.Windows.Documents.Block;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

namespace CcDirector.Wpf.Helpers;

/// <summary>
/// Converts a Markdown string into a WPF FlowDocument styled for the dark theme.
/// </summary>
public static class MarkdownFlowDocumentRenderer
{
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private static readonly Brush H1Brush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)));
    private static readonly Brush H2Brush = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)));
    private static readonly Brush H3Brush = Freeze(new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)));
    private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)));
    private static readonly Brush CodeForeground = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)));
    private static readonly Brush LinkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)));
    private static readonly Brush QuoteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)));
    private static readonly Brush QuoteBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)));
    private static readonly Brush DocBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
    private static readonly Brush TableHeaderForeground = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));
    private static readonly Brush TableAltRowBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)));

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");
    private static readonly FontFamily UiFont = new("Segoe UI");

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static FlowDocument Render(string markdown) => Render(markdown, embedded: false);

    /// <summary>
    /// Renders markdown to a FlowDocument.
    /// When <paramref name="embedded"/> is true, uses transparent background and zero padding (for chat bubbles).
    /// </summary>
    public static FlowDocument Render(string markdown, bool embedded)
    {
        var doc = new FlowDocument
        {
            Background = embedded ? Brushes.Transparent : DocBackground,
            Foreground = TextBrush,
            FontFamily = UiFont,
            FontSize = 14,
            PagePadding = embedded ? new Thickness(0) : new Thickness(24, 16, 24, 16)
        };

        if (string.IsNullOrEmpty(markdown))
            return doc;

        var parsed = Markdown.Parse(markdown, Pipeline);

        foreach (var block in parsed)
        {
            var element = RenderBlock(block);
            if (element != null)
                doc.Blocks.Add(element);
        }

        return doc;
    }

    private static WpfBlock? RenderBlock(MarkdownObject block)
    {
        return block switch
        {
            HeadingBlock heading => RenderHeading(heading),
            ParagraphBlock paragraph => RenderParagraph(paragraph),
            FencedCodeBlock fencedCode => RenderCodeBlock(fencedCode),
            CodeBlock codeBlock => RenderCodeBlock(codeBlock),
            ListBlock list => RenderList(list),
            QuoteBlock quote => RenderQuote(quote),
            Markdig.Extensions.Tables.Table table => RenderTable(table),
            ThematicBreakBlock => RenderThematicBreak(),
            _ => RenderFallbackBlock(block)
        };
    }

    private static Paragraph RenderHeading(HeadingBlock heading)
    {
        var para = new Paragraph
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 6)
        };

        switch (heading.Level)
        {
            case 1:
                para.FontSize = 24;
                para.Foreground = H1Brush;
                para.Margin = new Thickness(0, 16, 0, 8);
                break;
            case 2:
                para.FontSize = 20;
                para.Foreground = H2Brush;
                para.Margin = new Thickness(0, 14, 0, 6);
                break;
            case 3:
                para.FontSize = 16;
                para.Foreground = H3Brush;
                break;
            case 4:
                para.FontSize = 14;
                para.Foreground = H3Brush;
                break;
            default:
                para.FontSize = 13;
                para.Foreground = H3Brush;
                break;
        }

        if (heading.Inline != null)
            AddInlines(para.Inlines, heading.Inline);

        return para;
    }

    private static Paragraph RenderParagraph(ParagraphBlock paragraph)
    {
        var para = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = TextBrush
        };

        if (paragraph.Inline != null)
            AddInlines(para.Inlines, paragraph.Inline);

        return para;
    }

    private static Paragraph RenderCodeBlock(CodeBlock codeBlock)
    {
        var text = ExtractCodeBlockText(codeBlock);

        var para = new Paragraph
        {
            FontFamily = MonoFont,
            FontSize = 13,
            Foreground = CodeForeground,
            Background = CodeBackground,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 8),
            BorderBrush = QuoteBorderBrush,
            BorderThickness = new Thickness(1)
        };

        para.Inlines.Add(new Run(text));
        return para;
    }

    private static List RenderList(ListBlock listBlock)
    {
        var list = new List
        {
            Foreground = TextBrush,
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(24, 0, 0, 0)
        };

        if (listBlock.IsOrdered)
            list.MarkerStyle = TextMarkerStyle.Decimal;
        else
            list.MarkerStyle = TextMarkerStyle.Disc;

        foreach (var item in listBlock)
        {
            if (item is ListItemBlock listItem)
            {
                var listItemElement = new ListItem();
                foreach (var child in listItem)
                {
                    var rendered = RenderBlock(child);
                    if (rendered != null)
                        listItemElement.Blocks.Add(rendered);
                }
                list.ListItems.Add(listItemElement);
            }
        }

        return list;
    }

    private static Section RenderQuote(QuoteBlock quote)
    {
        var section = new Section
        {
            BorderBrush = QuoteBorderBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 0, 4),
            Margin = new Thickness(0, 4, 0, 8),
            Foreground = QuoteBrush
        };

        foreach (var child in quote)
        {
            var rendered = RenderBlock(child);
            if (rendered != null)
                section.Blocks.Add(rendered);
        }

        return section;
    }

    private static WpfTable RenderTable(Markdig.Extensions.Tables.Table table)
    {
        var wpfTable = new WpfTable
        {
            CellSpacing = 0,
            BorderBrush = QuoteBorderBrush,
            BorderThickness = new Thickness(1),
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
            wpfTable.Columns.Add(new TableColumn());

        var rowGroup = new TableRowGroup();
        var rowIndex = 0;

        foreach (var child in table)
        {
            if (child is not MdTableRow mdRow)
                continue;

            var wpfRow = new WpfTableRow();

            if (mdRow.IsHeader)
            {
                wpfRow.Foreground = TableHeaderForeground;
                wpfRow.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                wpfRow.Foreground = TextBrush;
                if (rowIndex % 2 == 1)
                    wpfRow.Background = TableAltRowBackground;
            }

            foreach (var cellChild in mdRow)
            {
                if (cellChild is not MdTableCell mdCell)
                    continue;

                var wpfCell = new WpfTableCell
                {
                    BorderBrush = QuoteBorderBrush,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(8, 4, 8, 4)
                };

                foreach (var block in mdCell)
                {
                    var rendered = RenderBlock(block);
                    if (rendered != null)
                        wpfCell.Blocks.Add(rendered);
                }

                wpfRow.Cells.Add(wpfCell);
            }

            rowGroup.Rows.Add(wpfRow);
            if (!mdRow.IsHeader)
                rowIndex++;
        }

        wpfTable.RowGroups.Add(rowGroup);
        return wpfTable;
    }

    private static Paragraph RenderThematicBreak()
    {
        return new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 8)
        };
    }

    private static WpfBlock? RenderFallbackBlock(MarkdownObject block)
    {
        // For any unhandled block type, try to extract text content
        if (block is LeafBlock leaf && leaf.Inline != null)
        {
            var para = new Paragraph
            {
                Foreground = TextBrush,
                Margin = new Thickness(0, 0, 0, 8)
            };
            AddInlines(para.Inlines, leaf.Inline);
            return para;
        }

        if (block is ContainerBlock container)
        {
            var section = new Section();
            foreach (var child in container)
            {
                var rendered = RenderBlock(child);
                if (rendered != null)
                    section.Blocks.Add(rendered);
            }
            return section.Blocks.Count > 0 ? section : null;
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
                        span.FontWeight = FontWeights.Bold;
                    else
                        span.FontStyle = FontStyles.Italic;
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
                        // Show image alt text as placeholder
                        var altText = link.FirstChild is LiteralInline altLiteral
                            ? altLiteral.Content.ToString()
                            : "[Image]";
                        target.Add(new Run($"[Image: {altText}]") { Foreground = QuoteBrush });
                    }
                    else
                    {
                        var hyperlink = new Span { Foreground = LinkBrush };
                        hyperlink.TextDecorations = TextDecorations.Underline;
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
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                builder.AppendLine();
            builder.Append(lines.Lines[i].Slice.ToString());
        }
        return builder.ToString().TrimEnd();
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
