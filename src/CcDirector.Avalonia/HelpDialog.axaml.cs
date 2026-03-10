using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class HelpDialog : Window
{
    public HelpDialog()
    {
        FileLog.Write("[HelpDialog] Constructor: initializing");
        InitializeComponent();
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[HelpDialog] BtnClose_Click: closing dialog");
        Close();
    }
}
