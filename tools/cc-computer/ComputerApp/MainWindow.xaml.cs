using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using CCComputer.App.ViewModels;

namespace CCComputer.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    // Win32 API declarations for window management
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_MINIMIZE = 6;
    private const uint GW_OWNER = 4;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // Auto-scroll the log when new entries are added
        _viewModel.LogEntries.CollectionChanged += LogEntries_CollectionChanged;

        // Set up agent mode layout when window loads
        Loaded += MainWindow_Loaded;

        // Dispose the ViewModel (and session logger) when window closes
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // Dispose the ViewModel to finalize the session log
        _viewModel.Dispose();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Minimize all other windows for a clean slate
        MinimizeAllOtherWindows();

        // Position CC Computer on the right 20% - slim control panel
        PositionOnRightSide();

        // Focus the input box
        InputTextBox.Focus();
    }

    private void MinimizeAllOtherWindows()
    {
        var currentProcessId = (uint)Environment.ProcessId;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            // Skip windows without titles
            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            // Skip owned windows (popups, dialogs)
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

            // Skip our own window
            GetWindowThreadProcessId(hWnd, out uint processId);
            if (processId == currentProcessId) return true;

            // Minimize this window
            ShowWindow(hWnd, SW_MINIMIZE);

            return true;
        }, IntPtr.Zero);
    }

    private void PositionOnRightSide()
    {
        // Get primary monitor working area (excludes taskbar)
        var workArea = System.Windows.SystemParameters.WorkArea;

        // Position on right 20% - just a slim control panel
        var panelWidth = workArea.Width * 0.20;
        Left = workArea.Left + workArea.Width - panelWidth;
        Top = workArea.Top;
        Width = panelWidth;
        Height = workArea.Height;

        // Ensure window state is Normal (not maximized)
        WindowState = WindowState.Normal;
    }

    private void LogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
        {
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
        }
    }
}
