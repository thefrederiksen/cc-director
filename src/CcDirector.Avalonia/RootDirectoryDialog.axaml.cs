using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

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
            ProviderCombo.SelectedIndex = 0;
        }

        Loaded += (_, _) => Dispatcher.UIThread.Post(() => LabelInput.Focus());
    }

    public RootDirectoryDialog() : this(null) { }

    private void ProviderCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AzureFieldsPanel is null) return;

        var selectedTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        AzureFieldsPanel.IsVisible = selectedTag == "AzureDevOps";
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Root Directory",
            AllowMultiple = false
        });

        if (result.Count > 0)
            PathInput.Text = result[0].Path.LocalPath;
    }

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RootDirectoryDialog] BtnSave_Click: validating input");

        var label = LabelInput.Text?.Trim() ?? "";
        var path = PathInput.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(label))
        {
            // TODO: Show validation message dialog
            FileLog.Write("[RootDirectoryDialog] Validation: label is empty");
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            FileLog.Write("[RootDirectoryDialog] Validation: path is empty");
            return;
        }

        if (!Directory.Exists(path))
        {
            FileLog.Write($"[RootDirectoryDialog] Validation: directory does not exist: {path}");
            return;
        }

        var selectedTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "LocalOnly";
        var provider = Enum.Parse<GitProvider>(selectedTag);

        string? azureOrg = null;
        string? azureProject = null;

        if (provider == GitProvider.AzureDevOps)
        {
            azureOrg = AzureOrgInput.Text?.Trim();
            azureProject = AzureProjectInput.Text?.Trim();

            if (string.IsNullOrWhiteSpace(azureOrg) || string.IsNullOrWhiteSpace(azureProject))
            {
                FileLog.Write("[RootDirectoryDialog] Validation: Azure DevOps fields are empty");
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
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
