using System.Windows;
using System.Windows.Controls;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Selects between plain text and rich FlowDocument templates for chat messages.
/// Assistant messages with rendered markdown use the RichTemplate; all others use PlainTemplate.
/// </summary>
public class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PlainTemplate { get; set; }
    public DataTemplate? RichTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is ChatMessageViewModel vm && vm.IsRichContent)
            return RichTemplate ?? PlainTemplate;

        return PlainTemplate;
    }
}
