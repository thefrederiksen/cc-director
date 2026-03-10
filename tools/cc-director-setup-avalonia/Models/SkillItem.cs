using System.ComponentModel;

namespace CcDirectorSetup.Models;

public class SkillItem : INotifyPropertyChanged
{
    private string _status = "Pending";

    public required string Name { get; init; }

    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
