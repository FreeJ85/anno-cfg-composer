using System.Windows;

namespace AnnoCfgComposer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            // Rendering uses a real window (Direct3D 11), so the message loop must be running
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () =>
            {
                try { await SelfTest.Run(e.Args[1], e.Args[2..]); }
                catch (Exception ex) { File.AppendAllText(Path.Combine(e.Args[1], "report.txt"), "\nFATAL " + ex); }
                Shutdown();
            });
            return;
        }

        // Unhandled errors are written to a file and reported (CrashLog)
        DispatcherUnhandledException += (_, args) =>
        {
            var file = CrashLog.Write(args.Exception, "UI");
            MessageBox.Show(Loc.F("dlg.errorBody", args.Exception.Message, file ?? "-"), Loc.S("dlg.errorCaption"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) CrashLog.Write(ex, "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "Task");
            args.SetObserved();
        };
        base.OnStartup(e);
        // --open <cfg path>: show that cfg right after startup (game path data/... or a disk file)
        // --mod <folder>: add that mod folder to the left list (same as drag and drop)
        int open = Array.IndexOf(e.Args, "--open");
        int mod = Array.IndexOf(e.Args, "--mod");
        new MainWindow(open >= 0 && open + 1 < e.Args.Length ? e.Args[open + 1] : null)
        {
            StartupMod = mod >= 0 && mod + 1 < e.Args.Length ? e.Args[mod + 1] : null,
        }.Show();
    }
}
