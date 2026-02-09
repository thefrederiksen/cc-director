using System.Windows;
using System.Windows.Input;

namespace CcDirector.Wpf;

public partial class RenameSessionDialog : Window
{
    public string? SessionName { get; private set; }

    public RenameSessionDialog(string currentName)
    {
        InitializeComponent();
        NameInput.Text = currentName;
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    private void NameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept()
    {
        SessionName = NameInput.Text;
        DialogResult = true;
    }
}
