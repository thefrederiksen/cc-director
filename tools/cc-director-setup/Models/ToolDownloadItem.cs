using System.ComponentModel;

namespace CcDirectorSetup.Models;

public class ToolDownloadItem : INotifyPropertyChanged
{
    private string _status = "Pending";
    private string _statusDetail = "";
    private double _progress;
    private string _sizeText = "";

    public required string Name { get; init; }
    public required string AssetName { get; init; }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(StatusColor)); }
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set { _statusDetail = value; OnPropertyChanged(nameof(StatusDetail)); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(nameof(Progress)); }
    }

    public string SizeText
    {
        get => _sizeText;
        set { _sizeText = value; OnPropertyChanged(nameof(SizeText)); }
    }

    public string StatusColor => Status switch
    {
        "Done" => "#22C55E",
        "Downloading" => "#007ACC",
        "Skipped" => "#888888",
        "Failed" => "#CC4444",
        "Locked" => "#E5A100",
        _ => "#CCCCCC"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
