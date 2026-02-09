using System.Windows;

namespace CcDirector.Wpf;

public partial class CloseDialog : Window
{
    public bool ShutDownCommandWindows => ShutDownCheckBox.IsChecked == true;

    public CloseDialog(int sessionCount)
    {
        InitializeComponent();
        MessageText.Text = $"Closing CC Director with {sessionCount} active session(s).";
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
