using System.Windows;

namespace CcDirector.Wpf;

public partial class WorkflowConfirmDialog : Window
{
    public WorkflowConfirmDialog()
    {
        InitializeComponent();
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
