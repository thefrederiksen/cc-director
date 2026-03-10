using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CcDirector.Avalonia.Controls;

public partial class InputDialog : Window
{
    public string InputText => InputTextBox.Text ?? string.Empty;

    public InputDialog(string title, string label, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        InputTextBox.Text = defaultValue;

        Loaded += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                InputTextBox.Focus();
                InputTextBox.SelectAll();
            });
        };
    }

    // Parameterless constructor for XAML designer
    public InputDialog() : this("Input", "", "") { }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void InputTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Close(true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(false);
        }
    }
}
