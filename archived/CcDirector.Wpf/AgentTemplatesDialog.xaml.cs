using System.IO;
using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.Wpf;

public partial class AgentTemplatesDialog : Window
{
    private readonly AgentTemplateStore _store;
    private AgentTemplate? _selectedTemplate;
    private bool _suppressSelectionChanged;

    /// <summary>
    /// Fired when the user clicks "Launch on Project..." with a selected template and chosen repo path.
    /// </summary>
    public event Action<AgentTemplate, string>? LaunchRequested;

    public AgentTemplatesDialog(AgentTemplateStore store)
    {
        InitializeComponent();
        _store = store;
        RefreshList();
    }

    private void RefreshList(string? selectId = null)
    {
        _suppressSelectionChanged = true;

        var items = _store.Templates.ToList();
        TemplateList.ItemsSource = items;

        if (selectId != null)
        {
            var match = items.FindIndex(t => t.Id == selectId);
            if (match >= 0)
                TemplateList.SelectedIndex = match;
        }
        else if (items.Count > 0 && TemplateList.SelectedIndex < 0)
        {
            TemplateList.SelectedIndex = 0;
        }

        _suppressSelectionChanged = false;

        // Trigger population of edit form
        if (TemplateList.SelectedItem is AgentTemplate selected)
            PopulateForm(selected);
        else
            ClearForm();
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;

        if (TemplateList.SelectedItem is AgentTemplate template)
        {
            PopulateForm(template);
        }
        else
        {
            ClearForm();
        }
    }

    private void PopulateForm(AgentTemplate template)
    {
        _selectedTemplate = template;
        EditPanel.IsEnabled = true;

        TxtName.Text = template.Name;
        TxtDescription.Text = template.Description;
        SetComboBoxText(CmbModel, template.Model ?? "");
        TxtMaxTurns.Text = template.MaxTurns?.ToString() ?? "";
        TxtMaxBudget.Text = template.MaxBudgetUsd?.ToString() ?? "";
        TxtSystemPrompt.Text = template.SystemPrompt ?? "";
        TxtAppendSystemPrompt.Text = template.AppendSystemPrompt ?? "";
        SetComboBoxText(CmbPermissionMode, template.PermissionMode ?? "");
        ChkSkipPermissions.IsChecked = template.SkipPermissions;
        TxtTools.Text = template.Tools ?? "";
        TxtAllowedTools.Text = template.AllowedTools ?? "";
        TxtDisallowedTools.Text = template.DisallowedTools ?? "";
        TxtMcpConfigPath.Text = template.McpConfigPath ?? "";
    }

    private void ClearForm()
    {
        _selectedTemplate = null;
        EditPanel.IsEnabled = false;

        TxtName.Text = "";
        TxtDescription.Text = "";
        SetComboBoxText(CmbModel, "");
        TxtMaxTurns.Text = "";
        TxtMaxBudget.Text = "";
        TxtSystemPrompt.Text = "";
        TxtAppendSystemPrompt.Text = "";
        SetComboBoxText(CmbPermissionMode, "");
        ChkSkipPermissions.IsChecked = false;
        TxtTools.Text = "";
        TxtAllowedTools.Text = "";
        TxtDisallowedTools.Text = "";
        TxtMcpConfigPath.Text = "";
    }

    private AgentTemplate ReadFormIntoTemplate(AgentTemplate template)
    {
        template.Name = TxtName.Text.Trim();
        template.Description = TxtDescription.Text.Trim();
        template.Model = NullIfEmpty(GetComboBoxText(CmbModel));
        template.FallbackModel = null; // Not exposed in form yet
        template.MaxTurns = int.TryParse(TxtMaxTurns.Text.Trim(), out var turns) ? turns : null;
        template.MaxBudgetUsd = decimal.TryParse(TxtMaxBudget.Text.Trim(), out var budget) ? budget : null;
        template.SystemPrompt = NullIfEmpty(TxtSystemPrompt.Text);
        template.AppendSystemPrompt = NullIfEmpty(TxtAppendSystemPrompt.Text);
        template.PermissionMode = NullIfEmpty(GetComboBoxText(CmbPermissionMode));
        template.SkipPermissions = ChkSkipPermissions.IsChecked == true;
        template.Tools = NullIfEmpty(TxtTools.Text);
        template.AllowedTools = NullIfEmpty(TxtAllowedTools.Text);
        template.DisallowedTools = NullIfEmpty(TxtDisallowedTools.Text);
        template.McpConfigPath = NullIfEmpty(TxtMcpConfigPath.Text);
        return template;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnSave_Click");
        try
        {
            if (_selectedTemplate == null) return;

            ReadFormIntoTemplate(_selectedTemplate);

            if (string.IsNullOrWhiteSpace(_selectedTemplate.Name))
            {
                MessageBox.Show(this, "Name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _store.Update(_selectedTemplate);
            RefreshList(_selectedTemplate.Id);
            FileLog.Write($"[AgentTemplatesDialog] BtnSave_Click: saved template id={_selectedTemplate.Id}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnSave_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to save template:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnNew_Click");
        try
        {
            var template = new AgentTemplate
            {
                Name = "New Template",
                Description = "",
            };
            _store.Add(template);
            RefreshList(template.Id);
            TxtName.Focus();
            TxtName.SelectAll();
            FileLog.Write($"[AgentTemplatesDialog] BtnNew_Click: created id={template.Id}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnNew_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to create template:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDuplicate_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnDuplicate_Click");
        try
        {
            if (_selectedTemplate == null) return;

            var clone = _store.Duplicate(_selectedTemplate.Id);
            RefreshList(clone.Id);
            FileLog.Write($"[AgentTemplatesDialog] BtnDuplicate_Click: duplicated to id={clone.Id}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnDuplicate_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to duplicate template:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnDelete_Click");
        try
        {
            if (_selectedTemplate == null) return;

            var result = MessageBox.Show(this,
                $"Delete template '{_selectedTemplate.Name}'?",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            var id = _selectedTemplate.Id;
            _store.Remove(id);
            _selectedTemplate = null;
            RefreshList();
            FileLog.Write($"[AgentTemplatesDialog] BtnDelete_Click: deleted id={id}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnDelete_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to delete template:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnLaunch_Click");
        try
        {
            if (_selectedTemplate == null)
            {
                MessageBox.Show(this, "Select a template first.", "No Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new OpenFolderDialog
            {
                Title = "Select project directory to launch agent in",
            };

            if (dlg.ShowDialog(this) != true) return;

            var repoPath = dlg.FolderName;
            FileLog.Write($"[AgentTemplatesDialog] BtnLaunch_Click: template={_selectedTemplate.Name}, path={repoPath}");
            LaunchRequested?.Invoke(_selectedTemplate, repoPath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnLaunch_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to launch:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnImport_Click");
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import Agent Template",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            };

            if (dlg.ShowDialog(this) != true) return;

            var json = File.ReadAllText(dlg.FileName);
            var imported = _store.ImportFromJson(json);
            if (imported == null)
            {
                MessageBox.Show(this, "Could not parse the template file.", "Import Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshList(imported.Id);
            FileLog.Write($"[AgentTemplatesDialog] BtnImport_Click: imported '{imported.Name}'");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnImport_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to import template:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnExport_Click");
        try
        {
            if (_selectedTemplate == null)
            {
                MessageBox.Show(this, "Select a template first.", "No Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Export Agent Template",
                Filter = "JSON files (*.json)|*.json",
                FileName = $"{_selectedTemplate.Name.Replace(' ', '-').ToLowerInvariant()}.json",
            };

            if (dlg.ShowDialog(this) != true) return;

            var json = _store.ExportToJson(_selectedTemplate.Id);
            File.WriteAllText(dlg.FileName, json);
            FileLog.Write($"[AgentTemplatesDialog] BtnExport_Click: exported to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnExport_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to export template:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static void SetComboBoxText(ComboBox combo, string text)
    {
        // Try to select matching item
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (item.Content?.ToString() ?? "") == text)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        // If editable, set text directly
        if (combo.IsEditable)
        {
            combo.Text = text;
        }
        else
        {
            combo.SelectedIndex = 0; // Default to first (empty) item
        }
    }

    private static string GetComboBoxText(ComboBox combo)
    {
        if (combo.IsEditable)
            return combo.Text?.Trim() ?? "";

        if (combo.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? "";

        return "";
    }

    private static string? NullIfEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }
}
