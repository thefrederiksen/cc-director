using System.IO;
using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.DocumentLibrary.Views;

public partial class AddLibraryDialog : Window
{
    public string LibraryPath { get; private set; } = string.Empty;
    public string LibraryLabel { get; private set; } = string.Empty;
    public string LibraryCategory { get; private set; } = "business";
    public string? LibraryOwner { get; private set; }

    public AddLibraryDialog()
    {
        FileLog.Write("[AddLibraryDialog] Constructor");
        InitializeComponent();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AddLibraryDialog] BtnBrowse_Click");
        var dialog = new OpenFolderDialog
        {
            Title = "Select Document Library Folder",
        };

        if (dialog.ShowDialog(this) == true)
        {
            TxtPath.Text = dialog.FolderName;

            // Auto-fill label from folder name if empty
            if (string.IsNullOrWhiteSpace(TxtLabel.Text))
            {
                var dirName = Path.GetFileName(dialog.FolderName);
                if (!string.IsNullOrEmpty(dirName))
                    TxtLabel.Text = dirName;
            }
        }
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AddLibraryDialog] BtnAdd_Click");

        if (string.IsNullOrWhiteSpace(TxtPath.Text))
        {
            MessageBox.Show("Path is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtLabel.Text))
        {
            MessageBox.Show("Label is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(TxtPath.Text))
        {
            MessageBox.Show("Directory does not exist.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LibraryPath = TxtPath.Text.Trim();
        LibraryLabel = TxtLabel.Text.Trim();
        LibraryCategory = (CmbCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "business";
        LibraryOwner = string.IsNullOrWhiteSpace(TxtOwner.Text) ? null : TxtOwner.Text.Trim();

        FileLog.Write($"[AddLibraryDialog] Result: path={LibraryPath}, label={LibraryLabel}, category={LibraryCategory}");
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AddLibraryDialog] BtnCancel_Click");
        DialogResult = false;
    }
}
