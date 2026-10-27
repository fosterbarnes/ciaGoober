using System.Windows;
using CiaGoober.ViewModels;

namespace CiaGoober;

public partial class App : Application
{
    private const string _MUTEX_NAME = @"Local\ciaGoober.singleInstance";

    private Mutex? _instance;

    // Startup order: single instance, settings, stale job cleanup, registry (a new install starts
    // empty), main window. Any failure shows its message and exits.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, _MUTEX_NAME, out var first);
        if (!first)
        {
            _instance.Dispose();
            _instance = null;
            MessageBox.Show("ciaGoober is already running.", "ciaGoober", MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }
        try
        {
            var settings = SettingsViewModel.Load(AppPaths.Settings);
            ForwarderBuilder.DeleteStaleJobs();
            var registry = ForwarderRegistry.Open(AppPaths.Registry);
            // Application.MainWindow hides the type name here.
            var window = new CiaGoober.Windows.MainWindow
            {
                DataContext = new MainViewModel(settings, registry),
            };
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show($"ciaGoober could not start: {error.Message}", "ciaGoober",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
