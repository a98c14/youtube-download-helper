using System.Windows;

namespace YoutubeDownloadHelper.App;

public partial class App : Application
{
    // The shell (main window, single instance, engine supervisor) lands in #51. Until then, exit right away.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown();
    }
}
