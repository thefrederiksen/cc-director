using System.Windows;

namespace CcDirector.ContentWriter.Views;

public partial class NewDocumentDialog : Window
{
    public string DocumentName => TxtName.Text.Trim();

    public NewDocumentDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => TxtName.Focus();
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            TxtName.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
            return;
        }

        DialogResult = true;
    }
}
