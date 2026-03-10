using Avalonia.Controls;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FileLog.Write("[MainWindow] Avalonia MainWindow initialized");
    }
}
