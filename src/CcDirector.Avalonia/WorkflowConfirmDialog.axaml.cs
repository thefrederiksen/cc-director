using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CcDirector.Avalonia;

public partial class WorkflowConfirmDialog : Window
{
    public WorkflowConfirmDialog()
    {
        InitializeComponent();
    }

    private void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
