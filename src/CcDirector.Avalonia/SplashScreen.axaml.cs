using System;
using System.Reflection;
using Avalonia.Controls;

namespace CcDirector.Avalonia;

public partial class SplashScreen : Window
{
    public SplashScreen()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is not null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v1.0.0";

        VersionText.Text = $"Version {versionText}";
        DateText.Text = DateTime.Now.ToString("MMMM d, yyyy");
    }
}
