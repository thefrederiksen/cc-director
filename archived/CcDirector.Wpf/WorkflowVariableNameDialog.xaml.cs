using System.Windows;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class WorkflowVariableNameDialog : Window
{
    public string VariableName { get; private set; } = "";

    public WorkflowVariableNameDialog(string paramKey, string currentValue)
    {
        InitializeComponent();
        FileLog.Write($"[WorkflowVariableNameDialog] Created: paramKey={paramKey}");

        CurrentValueText.Text = currentValue.Length > 80 ? currentValue[..80] + "..." : currentValue;
        VarNameBox.Text = paramKey;
        VarNameBox.SelectAll();

        Loaded += (_, _) => VarNameBox.Focus();
    }

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[WorkflowVariableNameDialog] BtnOK_Click");

        var name = VarNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Enter a variable name.", "Variable Name",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        VariableName = name;
        DialogResult = true;
        Close();
    }
}
