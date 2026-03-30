using System.Windows;
using System.Windows.Input;

namespace CcDirector.Wpf.Voice;

/// <summary>
/// Dialog for typing input when microphone is unavailable.
/// </summary>
public partial class TextInputDialog : Window
{
    /// <summary>
    /// The text entered by the user.
    /// </summary>
    public string InputText { get; private set; } = string.Empty;

    public TextInputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
        };
    }

    private void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        Submit();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Submit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            Close();
        }
    }

    private void Submit()
    {
        var text = InputTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        InputText = text;
        DialogResult = true;
        Close();
    }
}
