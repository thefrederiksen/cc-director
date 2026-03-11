using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class WorkflowVariableNameDialog : Window
{
    public string VariableName { get; private set; } = "";

    public WorkflowVariableNameDialog(string paramKey, string currentValue)
    {
        InitializeComponent();
        FileLog.Write($"[WorkflowVariableNameDialog] Created: paramKey={paramKey}");

        CurrentValueText.Text = currentValue.Length > 80 ? currentValue[..80] + "..." : currentValue;
        VarNameBox.Text = paramKey;

        Loaded += (_, _) =>
        {
            VarNameBox.Focus();
            VarNameBox.SelectAll();
        };
    }

    private void BtnOK_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[WorkflowVariableNameDialog] BtnOK_Click");

        var name = VarNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            FileLog.Write("[WorkflowVariableNameDialog] BtnOK_Click: empty name");
            return;
        }

        VariableName = name;
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
