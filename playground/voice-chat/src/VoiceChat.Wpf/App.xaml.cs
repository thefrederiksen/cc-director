using System.Windows;
using VoiceChat.Core.Logging;

namespace VoiceChat.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        VoiceLog.Start();
        VoiceLog.Write("[App] Voice Chat starting up.");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        VoiceLog.Write("[App] Voice Chat shutting down.");
        VoiceLog.Stop();
        base.OnExit(e);
    }
}
