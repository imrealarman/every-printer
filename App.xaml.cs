using EveryPrinter.Services;
using Microsoft.UI.Xaml;

namespace EveryPrinter;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
        if (!SingleInstance.TryBecomePrimary())
        {
            // Already running: hand the files over and leave.
            SingleInstance.Forward(files);
            Exit();
            return;
        }

        window = new MainWindow();
        SingleInstance.Listen(paths => window.DispatcherQueue.TryEnqueue(() => _ = window.ReceiveAsync(paths)));
        window.Activate();
    }
}
