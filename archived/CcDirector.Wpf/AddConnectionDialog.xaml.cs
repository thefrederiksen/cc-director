using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

/// <summary>
/// Dialog for adding a new browser connection.
/// Validates name as lowercase alphanumeric with hyphens.
/// </summary>
public partial class AddConnectionDialog : Window
{
    private static readonly Regex ValidName = new("^[a-z0-9][a-z0-9-]*$");

    public string ConnectionName { get; private set; } = "";
    public string ConnectionDescription { get; private set; } = "";
    public string? ConnectionUrl { get; private set; }
    public string? ConnectionTool { get; private set; }

    public AddConnectionDialog()
    {
        InitializeComponent();
        FileLog.Write("[AddConnectionDialog] Opened");
    }

    private void TxtName_TextChanged(object sender, TextChangedEventArgs e)
    {
        var name = TxtName.Text.Trim();

        if (string.IsNullOrEmpty(name))
        {
            NameError.Visibility = Visibility.Collapsed;
            BtnOk.IsEnabled = false;
            return;
        }

        if (!ValidName.IsMatch(name))
        {
            NameError.Text = "Use lowercase letters, numbers, and hyphens only";
            NameError.Visibility = Visibility.Visible;
            BtnOk.IsEnabled = false;
            return;
        }

        if (name.Length > 50)
        {
            NameError.Text = "Name must be 50 characters or less";
            NameError.Visibility = Visibility.Visible;
            BtnOk.IsEnabled = false;
            return;
        }

        NameError.Visibility = Visibility.Collapsed;
        BtnOk.IsEnabled = true;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        ConnectionName = TxtName.Text.Trim();
        ConnectionDescription = TxtDescription.Text.Trim();
        ConnectionUrl = string.IsNullOrWhiteSpace(TxtUrl.Text) ? null : TxtUrl.Text.Trim();

        var selectedTool = (CmbTool.SelectedItem as ComboBoxItem)?.Content?.ToString();
        ConnectionTool = selectedTool == "(none)" ? null : selectedTool;

        FileLog.Write($"[AddConnectionDialog] OK: name={ConnectionName}, url={ConnectionUrl}, tool={ConnectionTool}");

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AddConnectionDialog] Cancelled");
        DialogResult = false;
        Close();
    }
}
