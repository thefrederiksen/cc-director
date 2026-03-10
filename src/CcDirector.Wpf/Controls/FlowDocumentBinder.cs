using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Attached property to bind a FlowDocument to a RichTextBox.
/// WPF's RichTextBox.Document is not a DependencyProperty, so direct binding is not possible.
/// Usage: local:FlowDocumentBinder.Document="{Binding RenderedDocument}"
/// </summary>
public static class FlowDocumentBinder
{
    public static readonly DependencyProperty DocumentProperty =
        DependencyProperty.RegisterAttached(
            "Document",
            typeof(FlowDocument),
            typeof(FlowDocumentBinder),
            new PropertyMetadata(null, OnDocumentChanged));

    public static FlowDocument? GetDocument(DependencyObject obj)
        => (FlowDocument?)obj.GetValue(DocumentProperty);

    public static void SetDocument(DependencyObject obj, FlowDocument? value)
        => obj.SetValue(DocumentProperty, value);

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox rtb)
            return;

        if (e.NewValue is FlowDocument doc)
            rtb.Document = doc;
        else
            rtb.Document = new FlowDocument();
    }
}
