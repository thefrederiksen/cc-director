using System.Windows;
using System.Windows.Input;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Simple text input dialog for Quick Actions (rename thread, etc.).
/// </summary>
public partial class InputDialog : Window
{
    public string InputText => InputTextBox.Text;

    public InputDialog(string title, string label, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        InputTextBox.Text = defaultValue;

        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DialogResult = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }
}
