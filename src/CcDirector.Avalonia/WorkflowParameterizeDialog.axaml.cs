using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Browser;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class WorkflowParameterizeDialog : Window
{
    private readonly WorkflowTemplate _template;
    private readonly List<StepValueEntry> _entries = new();

    public bool Saved { get; private set; }

    public WorkflowParameterizeDialog(WorkflowTemplate template)
    {
        InitializeComponent();
        _template = template;
        FileLog.Write($"[WorkflowParameterizeDialog] Created for workflow: {template.Name}");

        BuildEntries();
        RefreshUI();
    }

    private void BuildEntries()
    {
        _entries.Clear();
        for (var i = 0; i < _template.Actions.Count; i++)
        {
            var action = _template.Actions[i];
            if (action.Params == null) continue;

            foreach (var kv in action.Params)
            {
                var rawValue = kv.Value?.ToString() ?? "";
                _entries.Add(new StepValueEntry
                {
                    ActionIndex = i,
                    StepIndex = $"#{i + 1}",
                    Command = action.Command,
                    ParamKey = kv.Key,
                    RawValue = rawValue,
                });
            }
        }
    }

    private void RefreshUI()
    {
        foreach (var entry in _entries)
        {
            var isParam = entry.RawValue.Contains("{") && entry.RawValue.Contains("}");
            entry.DisplayValue = entry.RawValue;
            entry.ValueColor = isParam
                ? new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))
                : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        }

        StepsPanel.ItemsSource = null;
        StepsPanel.ItemsSource = _entries;

        if (_template.Parameters.Count == 0)
        {
            ParamsSummary.Text = "None";
        }
        else
        {
            ParamsSummary.Text = string.Join(", ",
                _template.Parameters.Select(p => $"{{{p.Name}}}"));
        }
    }

    private async void BtnSetVar_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not StepValueEntry entry)
            return;

        FileLog.Write($"[WorkflowParameterizeDialog] BtnSetVar_Click: step={entry.StepIndex}, key={entry.ParamKey}");

        var dialog = new WorkflowVariableNameDialog(entry.ParamKey, entry.RawValue);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result != true)
            return;

        var varName = dialog.VariableName;
        var placeholder = $"{{{varName}}}";
        var originalValue = entry.RawValue;

        // Update the action param value
        var action = _template.Actions[entry.ActionIndex];
        if (action.Params != null)
        {
            action.Params[entry.ParamKey] = placeholder;
        }

        entry.RawValue = placeholder;

        // Add parameter definition if not already present
        if (!_template.Parameters.Any(p => p.Name == varName))
        {
            _template.Parameters.Add(new WorkflowParameter
            {
                Name = varName,
                Description = $"Value for {entry.Command} {entry.ParamKey}",
                DefaultValue = originalValue,
            });
        }

        FileLog.Write($"[WorkflowParameterizeDialog] Variable set: {varName} at step {entry.StepIndex}/{entry.ParamKey}");
        RefreshUI();
    }

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[WorkflowParameterizeDialog] BtnSave_Click: {_template.Parameters.Count} parameters");
        Saved = true;
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private class StepValueEntry
    {
        public int ActionIndex { get; set; }
        public string StepIndex { get; set; } = "";
        public string Command { get; set; } = "";
        public string ParamKey { get; set; } = "";
        public string RawValue { get; set; } = "";
        public string DisplayValue { get; set; } = "";
        public ISolidColorBrush ValueColor { get; set; } = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    }
}
