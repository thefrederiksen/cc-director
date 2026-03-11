using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class WorkflowConditionDialog : Window
{
    public string SelectedCheck { get; private set; } = "elementExists";
    public string? SelectorValue { get; private set; }
    public string? CheckValue { get; private set; }

    public WorkflowConditionDialog()
    {
        InitializeComponent();
        FileLog.Write("[WorkflowConditionDialog] Created");
    }

    private void CheckTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CheckTypeCombo.SelectedItem is not ComboBoxItem item) return;
        var check = item.Tag?.ToString() ?? "elementExists";

        switch (check)
        {
            case "elementExists":
                InputLabel.Text = "CSS Selector";
                InputBox.Text = "";
                break;
            case "urlContains":
                InputLabel.Text = "URL substring";
                InputBox.Text = "";
                break;
            case "textVisible":
                InputLabel.Text = "Text to find";
                InputBox.Text = "";
                break;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var input = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            MessageBox.Show("Enter a value for the condition.",
                "Condition", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = CheckTypeCombo.SelectedItem as ComboBoxItem;
        SelectedCheck = item?.Tag?.ToString() ?? "elementExists";

        if (SelectedCheck == "elementExists")
        {
            SelectorValue = input;
        }
        else
        {
            CheckValue = input;
        }

        FileLog.Write($"[WorkflowConditionDialog] OK: check={SelectedCheck}, selector={SelectorValue}, value={CheckValue}");

        DialogResult = true;
    }
}
