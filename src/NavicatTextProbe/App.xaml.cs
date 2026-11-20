using System.Configuration;
using System.Data;
using System.Windows;
using NavicatTextProbe.Services;

namespace NavicatTextProbe;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.FirstOrDefault() == ForegroundCaretLocator.HelperArgument)
        {
            Shutdown(Task.Run(() => ForegroundCaretLocator.RunHelper(e.Args)).GetAwaiter().GetResult());
            return;
        }

        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);
    }
}

