using System.Windows;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class HelpDialog : Window
{
    public HelpDialog()
    {
        FileLog.Write("[HelpDialog] Constructor: initializing");
        InitializeComponent();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[HelpDialog] BtnClose_Click: closing dialog");
        Close();
    }
}
