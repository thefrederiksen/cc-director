using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Instances;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class CreateInstanceDialog : Window
{
    /// <summary>The instance that was created, once the dialog closes with true.</summary>
    public NamedInstance? CreatedInstance { get; private set; }

    /// <summary>Whether the user asked to launch the new instance immediately.</summary>
    public bool LaunchAfter { get; private set; }

    public CreateInstanceDialog()
    {
        InitializeComponent();
        DisplayNameInput.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) UpdateSlugPreview();
        };
        Loaded += (_, _) => Dispatcher.UIThread.Post(() => DisplayNameInput.Focus());
    }

    private void UpdateSlugPreview()
    {
        var name = DisplayNameInput.Text ?? "";
        SlugPreview.Text = string.IsNullOrWhiteSpace(name)
            ? "slug: (from the name) · port: auto-assigned"
            : $"slug: {NamedInstanceRegistry.PreviewSlug(name)} · port: auto-assigned";
    }

    private void Input_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Create(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(false); }
    }

    private void BtnCreate_Click(object? sender, RoutedEventArgs e) => Create();

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Create()
    {
        var displayName = (DisplayNameInput.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            ShowError("Enter a display name.");
            return;
        }

        try
        {
            CreatedInstance = NamedInstanceRegistry.Create(
                displayName,
                (GatewayUrlInput.Text ?? "").Trim(),
                (GatewayTokenInput.Text ?? "").Trim());
            LaunchAfter = LaunchAfterCheck.IsChecked == true;
            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CreateInstanceDialog] Create FAILED: {ex.Message}");
            ShowError($"Could not create instance: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
