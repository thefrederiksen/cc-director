using System.ComponentModel;
using System.Windows;
using CcDirector.Core.Browser;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class WorkflowParametersDialog : Window
{
    private readonly List<ParameterEntry> _entries = new();

    /// <summary>Resolved parameter values after the user clicks Run.</summary>
    public Dictionary<string, string> ResolvedValues { get; } = new();

    public WorkflowParametersDialog(List<WorkflowParameter> parameters)
    {
        InitializeComponent();
        FileLog.Write($"[WorkflowParametersDialog] Created with {parameters.Count} parameters");

        foreach (var p in parameters)
        {
            _entries.Add(new ParameterEntry
            {
                Name = p.Name,
                Description = p.Description,
                Value = p.DefaultValue,
            });
        }

        ParamsPanel.ItemsSource = _entries;
    }

    private void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[WorkflowParametersDialog] BtnRun_Click");

        foreach (var entry in _entries)
        {
            ResolvedValues[entry.Name] = entry.Value;
        }

        DialogResult = true;
        Close();
    }

    private class ParameterEntry : INotifyPropertyChanged
    {
        private string _value = "";

        public string Name { get; set; } = "";
        public string Description { get; set; } = "";

        public string Value
        {
            get => _value;
            set
            {
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
