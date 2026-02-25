using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.Wpf;

public partial class RootDirectoryDialog : Window
{
    public RootDirectoryConfig? Result { get; private set; }

    public RootDirectoryDialog(RootDirectoryConfig? existing = null)
    {
        InitializeComponent();

        if (existing != null)
        {
            Title = "Edit Root Directory";
            LabelInput.Text = existing.Label;
            PathInput.Text = existing.Path;
            AzureOrgInput.Text = existing.AzureOrg ?? "https://dev.azure.com/";
            AzureProjectInput.Text = existing.AzureProject ?? "";

            // Select the matching provider combo item
            for (var i = 0; i < ProviderCombo.Items.Count; i++)
            {
                if (ProviderCombo.Items[i] is ComboBoxItem item &&
                    item.Tag is string tag &&
                    tag == existing.Provider.ToString())
                {
                    ProviderCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        else
        {
            ProviderCombo.SelectedIndex = 0; // GitHub default
        }

        Loaded += (_, _) => LabelInput.Focus();
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AzureFieldsPanel is null) return; // designer

        var selectedTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        AzureFieldsPanel.Visibility = selectedTag == "AzureDevOps"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Root Directory" };
        if (dialog.ShowDialog(this) == true)
            PathInput.Text = dialog.FolderName;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[RootDirectoryDialog] BtnSave_Click: validating input");

        var label = LabelInput.Text.Trim();
        var path = PathInput.Text.Trim();

        if (string.IsNullOrWhiteSpace(label))
        {
            MessageBox.Show(this, "Please enter a label.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Please enter a root path.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!System.IO.Directory.Exists(path))
        {
            MessageBox.Show(this, $"Directory does not exist:\n{path}", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "LocalOnly";
        var provider = Enum.Parse<GitProvider>(selectedTag);

        string? azureOrg = null;
        string? azureProject = null;

        if (provider == GitProvider.AzureDevOps)
        {
            azureOrg = AzureOrgInput.Text.Trim();
            azureProject = AzureProjectInput.Text.Trim();

            if (string.IsNullOrWhiteSpace(azureOrg))
            {
                MessageBox.Show(this, "Please enter the Azure DevOps organization URL.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(azureProject))
            {
                MessageBox.Show(this, "Please enter the Azure DevOps project name.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Result = new RootDirectoryConfig
        {
            Label = label,
            Path = path,
            Provider = provider,
            AzureOrg = azureOrg,
            AzureProject = azureProject
        };

        FileLog.Write($"[RootDirectoryDialog] BtnSave_Click: label={label}, path={path}, provider={provider}");
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
