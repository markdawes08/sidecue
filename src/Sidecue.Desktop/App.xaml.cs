using System.Windows;
using Sidecue.Desktop.Prototypes.Overlay;

namespace Sidecue.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0)
        {
            if (e.Args.Length != 2 || e.Args[0] != "--probe-overlay")
            {
                Shutdown(2);
                return;
            }
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var exitCode = await OverlayProbe.RunAsync(e.Args[1]);
                    Shutdown(exitCode);
                }
                catch (Exception)
                {
                    // An unusable diagnostic output path may prevent writing a report.
                    // Return failure to the probe script without an unhandled UI exception.
                    Shutdown(1);
                }
            });
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
