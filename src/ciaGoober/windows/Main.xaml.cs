using System.ComponentModel;
using System.Windows;
using CiaGoober.Helpers;
using CiaGoober.Tabs;
using CiaGoober.ViewModels;

namespace CiaGoober.Windows;

public partial class MainWindow : Window
{
    private const string _DS_TAB = "DS";
    private const string _GBA_TAB = "GBA";
    private const string _GB_TAB = "GB";
    private const string _NES_TAB = "NES";

    private readonly TabBar _tabBar;
    private bool _waitingForBuilds;

    public MainWindow()
    {
        InitializeComponent();
        _tabBar = new TabBar(PageTabs, new Dictionary<string, FrameworkElement>
        {
            [_DS_TAB] = DsTab,
            [_GBA_TAB] = GbaTab,
            [_GB_TAB] = GbTab,
            [_NES_TAB] = NesTab,
            ["Settings"] = SettingsTab,
            ["About"] = AboutTab,
        });
        WindowLocationStore.Restore(this);
        SourceInitialized += (_, _) => WindowsTitleBarTheme.ApplyImmersiveDarkMode(this);
    }

    private MainViewModel? Main => DataContext as MainViewModel;

    // DataContext is set after construction, so the saved tab is read here.
    private void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        _tabBar.Start(Main?.Settings.SelectedPage);

    // Files and folders dropped anywhere in the window; other drag data is left to the controls.
    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    // From Settings or About, opens the tab of the first added game.
    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (Main is not { } main || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        if (await main.AddPathsAsync(paths) is not { } system
            || _tabBar.SelectedName is _DS_TAB or _GBA_TAB or _GB_TAB or _NES_TAB) return;
        _tabBar.Select(system switch
        {
            GameSystem.Nds => _DS_TAB,
            GameSystem.Gba => _GBA_TAB,
            GameSystem.Gb => _GB_TAB,
            GameSystem.Nes => _NES_TAB,
            _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
        });
    }

    // Stays open while the SD receivers are copied, so SdCard.Install can restore the card on
    // failure. Cancels every build, then stays open until running builds stop, so none is cut
    // off while it replaces a CIA.
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_waitingForBuilds || Main?.Settings.ReceiversActionCommand.IsRunning == true)
        {
            e.Cancel = true;
            return;
        }
        WindowLocationStore.Save(this);
        if (Main is not { } main) return;
        main.Settings.SelectedPage = _tabBar.SelectedName;
        main.Shutdown();
        if (main.BuildQueue.Pending == 0) return;
        e.Cancel = true;
        _waitingForBuilds = true;
        IsEnabled = false;
        main.StatusMessage = "Closing when the running builds stop";
        main.BuildQueue.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(BuildQueue.Pending) || main.BuildQueue.Pending != 0) return;
            _waitingForBuilds = false;
            // Leaves the queue's own bookkeeping before the window goes away.
            Dispatcher.BeginInvoke(Close);
        };
    }
}
