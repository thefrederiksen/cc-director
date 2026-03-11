using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class AgentTemplatesDialog : Window
{
    private readonly AgentTemplateStore _store;
    private AgentTemplate? _selectedTemplate;
    private bool _suppressSelectionChanged;

    private static readonly string[] ModelItems = ["", "haiku", "sonnet", "opus"];
    private static readonly string[] PermissionModeItems = ["", "plan", "acceptEdits", "bypassPermissions"];

    public event Action<AgentTemplate, string>? LaunchRequested;

    public AgentTemplatesDialog(AgentTemplateStore store)
    {
        InitializeComponent();
        _store = store;

        CmbModel.ItemsSource = ModelItems;
        CmbPermissionMode.ItemsSource = PermissionModeItems;

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

        if (TemplateList.SelectedItem is AgentTemplate selected)
            PopulateForm(selected);
        else
            ClearForm();
    }

    private void TemplateList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;

        if (TemplateList.SelectedItem is AgentTemplate template)
            PopulateForm(template);
        else
            ClearForm();
    }

    private void PopulateForm(AgentTemplate template)
    {
        _selectedTemplate = template;
        EditPanel.IsEnabled = true;

        TxtName.Text = template.Name;
        TxtDescription.Text = template.Description;
        SetComboValue(CmbModel, template.Model ?? "");
        TxtMaxTurns.Text = template.MaxTurns?.ToString() ?? "";
        TxtMaxBudget.Text = template.MaxBudgetUsd?.ToString() ?? "";
        TxtSystemPrompt.Text = template.SystemPrompt ?? "";
        TxtAppendSystemPrompt.Text = template.AppendSystemPrompt ?? "";
        SetComboValue(CmbPermissionMode, template.PermissionMode ?? "");
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
        SetComboValue(CmbModel, "");
        TxtMaxTurns.Text = "";
        TxtMaxBudget.Text = "";
        TxtSystemPrompt.Text = "";
        TxtAppendSystemPrompt.Text = "";
        SetComboValue(CmbPermissionMode, "");
        ChkSkipPermissions.IsChecked = false;
        TxtTools.Text = "";
        TxtAllowedTools.Text = "";
        TxtDisallowedTools.Text = "";
        TxtMcpConfigPath.Text = "";
    }

    private AgentTemplate ReadFormIntoTemplate(AgentTemplate template)
    {
        template.Name = TxtName.Text?.Trim() ?? "";
        template.Description = TxtDescription.Text?.Trim() ?? "";
        template.Model = NullIfEmpty(CmbModel.SelectedItem as string);
        template.FallbackModel = null;
        template.MaxTurns = int.TryParse(TxtMaxTurns.Text?.Trim(), out var turns) ? turns : null;
        template.MaxBudgetUsd = decimal.TryParse(TxtMaxBudget.Text?.Trim(), out var budget) ? budget : null;
        template.SystemPrompt = NullIfEmpty(TxtSystemPrompt.Text);
        template.AppendSystemPrompt = NullIfEmpty(TxtAppendSystemPrompt.Text);
        template.PermissionMode = NullIfEmpty(CmbPermissionMode.SelectedItem as string);
        template.SkipPermissions = ChkSkipPermissions.IsChecked == true;
        template.Tools = NullIfEmpty(TxtTools.Text);
        template.AllowedTools = NullIfEmpty(TxtAllowedTools.Text);
        template.DisallowedTools = NullIfEmpty(TxtDisallowedTools.Text);
        template.McpConfigPath = NullIfEmpty(TxtMcpConfigPath.Text);
        return template;
    }

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnSave_Click");
        try
        {
            if (_selectedTemplate == null) return;

            ReadFormIntoTemplate(_selectedTemplate);

            if (string.IsNullOrWhiteSpace(_selectedTemplate.Name))
            {
                FileLog.Write("[AgentTemplatesDialog] BtnSave_Click: name is empty");
                return;
            }

            _store.Update(_selectedTemplate);
            RefreshList(_selectedTemplate.Id);
            FileLog.Write($"[AgentTemplatesDialog] BtnSave_Click: saved template id={_selectedTemplate.Id}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnSave_Click FAILED: {ex.Message}");
        }
    }

    private void BtnNew_Click(object? sender, RoutedEventArgs e)
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
        }
    }

    private void BtnDuplicate_Click(object? sender, RoutedEventArgs e)
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
        }
    }

    private void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnDelete_Click");
        try
        {
            if (_selectedTemplate == null) return;

            var id = _selectedTemplate.Id;
            _store.Remove(id);
            _selectedTemplate = null;
            RefreshList();
            FileLog.Write($"[AgentTemplatesDialog] BtnDelete_Click: deleted id={id}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnDelete_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnLaunch_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnLaunch_Click");
        try
        {
            if (_selectedTemplate == null)
            {
                FileLog.Write("[AgentTemplatesDialog] BtnLaunch_Click: no template selected");
                return;
            }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select project directory to launch agent in",
                AllowMultiple = false,
            });

            if (folders.Count == 0) return;

            var repoPath = folders[0].Path.LocalPath;
            FileLog.Write($"[AgentTemplatesDialog] BtnLaunch_Click: template={_selectedTemplate.Name}, path={repoPath}");
            LaunchRequested?.Invoke(_selectedTemplate, repoPath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnLaunch_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnImport_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnImport_Click");
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Agent Template",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } },
                },
            });

            if (files.Count == 0) return;

            var json = await File.ReadAllTextAsync(files[0].Path.LocalPath);
            var imported = _store.ImportFromJson(json);
            if (imported == null)
            {
                FileLog.Write("[AgentTemplatesDialog] BtnImport_Click: could not parse template file");
                return;
            }

            RefreshList(imported.Id);
            FileLog.Write($"[AgentTemplatesDialog] BtnImport_Click: imported '{imported.Name}'");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnImport_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnExport_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentTemplatesDialog] BtnExport_Click");
        try
        {
            if (_selectedTemplate == null)
            {
                FileLog.Write("[AgentTemplatesDialog] BtnExport_Click: no template selected");
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Agent Template",
                SuggestedFileName = $"{_selectedTemplate.Name.Replace(' ', '-').ToLowerInvariant()}.json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } },
                },
            });

            if (file == null) return;

            var json = _store.ExportToJson(_selectedTemplate.Id);
            await File.WriteAllTextAsync(file.Path.LocalPath, json);
            FileLog.Write($"[AgentTemplatesDialog] BtnExport_Click: exported to {file.Path.LocalPath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplatesDialog] BtnExport_Click FAILED: {ex.Message}");
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static void SetComboValue(ComboBox combo, string value)
    {
        var items = combo.ItemsSource as string[];
        if (items == null) return;

        var index = Array.IndexOf(items, value);
        combo.SelectedIndex = index >= 0 ? index : 0;
    }

    private static string? NullIfEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }
}
