using System.Windows;
using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.Wpf;

public partial class WindowsTerminalWarningDialog : Window
{
    private const string ConhostGuid = "{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}";

    public WindowsTerminalWarningDialog()
    {
        InitializeComponent();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        // Set legacy conhost as the default terminal
        if (SetLegacyConhostAsDefault())
        {
            DialogResult = true; // Proceed with session start
        }
        else
        {
            MessageBox.Show(
                "Failed to change the terminal setting. Please change it manually in Windows Settings:\n\n" +
                "Settings -> System -> For Developers -> Terminal -> Windows Console Host",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            DialogResult = false;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    /// Set legacy Windows Console Host as the default terminal via registry.
    /// </summary>
    private static bool SetLegacyConhostAsDefault()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Console\%%Startup", writable: true);
            if (key == null)
            {
                // Key doesn't exist - create it
                using var newKey = Registry.CurrentUser.CreateSubKey(@"Console\%%Startup");
                if (newKey == null)
                {
                    FileLog.Write("[WindowsTerminalWarningDialog] Failed to create Console\\%%Startup key");
                    return false;
                }
                newKey.SetValue("DelegationConsole", ConhostGuid);
                newKey.SetValue("DelegationTerminal", ConhostGuid);
            }
            else
            {
                key.SetValue("DelegationConsole", ConhostGuid);
                key.SetValue("DelegationTerminal", ConhostGuid);
            }

            FileLog.Write("[WindowsTerminalWarningDialog] Set legacy conhost as default terminal");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WindowsTerminalWarningDialog] Failed to set terminal setting: {ex.Message}");
            return false;
        }
    }
}
