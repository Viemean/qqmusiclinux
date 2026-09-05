using Terminal.Gui.App;
using Terminal.Gui.Views;
using QQMusic.Tui.Player;
using QQMusic.Tui.UI;

namespace QQMusic.Tui;

public static class Program
{
    public static void Main(string[] args)
    {
        bool isDebug = args.Contains("--debug") || args.Contains("-d") ||
                       Environment.GetEnvironmentVariable("QQMUSIC_DEBUG") == "1";
        QQMusic.Tui.Utils.AppLogger.Init(isDebug);
        QQMusic.Tui.Utils.AppLogger.Info("Program", $"Starting QQ Music TUI. DebugMode: {isDebug}");

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            QQMusic.Tui.Utils.AppLogger.Error("Crash", $"Unhandled AppDomain exception: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            QQMusic.Tui.Utils.AppLogger.Error("Crash", $"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };




        using var player = new GstPlayer();
        player.Initialize();

        Application.Init();
        if (Application.Driver != null)
        {
            Application.Driver.Force16Colors = false;
        }
        MikuTheme.Apply();

        var mainWindow = new MainWindow(player);

        Application.Run(mainWindow);
        Application.Shutdown();
    }
}