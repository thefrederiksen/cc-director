using Avalonia.Controls;

namespace CcDirector.Avalonia;

public partial class WorkspaceProgressDialog : Window
{
    public WorkspaceProgressDialog(string workspaceName)
    {
        InitializeComponent();
        StatusText.Text = $"Loading workspace \"{workspaceName}\"...";
    }

    public void UpdateProgress(int current, int total, string sessionName)
    {
        StatusText.Text = $"Loading workspace - session {current}/{total}";
        ProgressBar.Value = (double)current / total * 100;
        DetailText.Text = sessionName;
    }

    public void SetClosing()
    {
        StatusText.Text = "Closing existing sessions...";
        ProgressBar.IsIndeterminate = true;
        DetailText.Text = "";
    }

    public void SetComplete()
    {
        StatusText.Text = "Workspace loaded";
        ProgressBar.Value = 100;
        DetailText.Text = "";
    }
}
