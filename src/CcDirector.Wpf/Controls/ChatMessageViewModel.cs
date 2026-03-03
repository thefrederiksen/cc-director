using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CcDirector.Core.Claude;
using CcDirector.Wpf.Helpers;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Wraps a ChatMessage for WPF data binding in the Simple Chat view.
/// </summary>
public sealed class ChatMessageViewModel
{
    // Frozen brushes -- created once, shared by all instances
    private static readonly SolidColorBrush UserBubbleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71)));
    private static readonly SolidColorBrush AssistantBubbleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)));
    private static readonly SolidColorBrush PermissionBubbleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x1B, 0x1B)));
    private static readonly SolidColorBrush TransparentBrush = Freeze(new SolidColorBrush(Colors.Transparent));

    private static readonly SolidColorBrush WhiteForeground = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush TextForeground = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private static readonly SolidColorBrush StatusForeground = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly SolidColorBrush PermissionForeground = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));

    private readonly FlowDocument? _renderedDocument;

    public ChatMessage Message { get; }

    public ChatMessageViewModel(ChatMessage message)
    {
        Message = message;

        if (message.Type == ChatMessageType.Assistant)
            _renderedDocument = MarkdownFlowDocumentRenderer.Render(message.Text, embedded: true);
    }

    /// <summary>
    /// Pre-rendered FlowDocument for assistant messages with rich markdown content.
    /// Returns null for non-assistant messages.
    /// </summary>
    public FlowDocument? RenderedDocument => _renderedDocument;

    /// <summary>True when this message should use the rich content template.</summary>
    public bool IsRichContent => _renderedDocument != null;

    public string Text => Message.Text;

    public string Timestamp => Message.Timestamp.ToLocalTime().ToString("HH:mm");

    public HorizontalAlignment Alignment => Message.Type == ChatMessageType.User
        ? HorizontalAlignment.Right
        : HorizontalAlignment.Left;

    public SolidColorBrush BubbleBackground => Message.Type switch
    {
        ChatMessageType.User => UserBubbleBrush,
        ChatMessageType.Assistant => AssistantBubbleBrush,
        ChatMessageType.PermissionNotice => PermissionBubbleBrush,
        ChatMessageType.Status => TransparentBrush,
        _ => TransparentBrush,
    };

    public SolidColorBrush TextForegroundBrush => Message.Type switch
    {
        ChatMessageType.User => WhiteForeground,
        ChatMessageType.Assistant => TextForeground,
        ChatMessageType.PermissionNotice => PermissionForeground,
        ChatMessageType.Status => StatusForeground,
        _ => TextForeground,
    };

    public double FontSize => Message.Type == ChatMessageType.Status ? 10 : 13;

    public FontStyle FontStyleValue => Message.Type == ChatMessageType.Status
        ? FontStyles.Italic
        : FontStyles.Normal;

    public CornerRadius BubbleCornerRadius => Message.Type == ChatMessageType.Status
        ? new CornerRadius(0)
        : new CornerRadius(8);

    public Thickness BubblePadding => Message.Type == ChatMessageType.Status
        ? new Thickness(4, 2, 4, 2)
        : new Thickness(12, 8, 12, 8);

    /// <summary>User bubbles get left margin (push right), assistant/status get right margin (push left).</summary>
    public Thickness BubbleMargin => Message.Type == ChatMessageType.User
        ? new Thickness(80, 4, 8, 4)
        : Message.Type == ChatMessageType.Status
            ? new Thickness(8, 2, 80, 2)
            : new Thickness(8, 4, 80, 4);

    public Visibility TimestampVisibility => Message.Type == ChatMessageType.Status
        ? Visibility.Collapsed
        : Visibility.Visible;

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
