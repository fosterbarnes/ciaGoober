using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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

    private const int _WM_DEVICECHANGE = 0x0219;
    private const int _DBT_DEVICEARRIVAL = 0x8000;
    private const int _DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private static readonly TimeSpan _CARD_SETTLE = TimeSpan.FromSeconds(1);

    private readonly TabBar _tabBar;
    private readonly DispatcherTimer _cardRefresh;
    private bool _waitingForBuilds;
    private bool _waitingForCopy;

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
        _cardRefresh = new DispatcherTimer { Interval = _CARD_SETTLE };
        _cardRefresh.Tick += (_, _) =>
        {
            _cardRefresh.Stop();
            if (Main is { } main) _ = main.Settings.Card.RefreshAsync();
        };
        SourceInitialized += (_, _) =>
        {
            WindowsTitleBarTheme.ApplyImmersiveDarkMode(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WndProc);
        };
    }

    private MainViewModel? Main => DataContext as MainViewModel;

    // DataContext is set after construction, so the saved tab and the SD card are read here.
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _tabBar.Start(Main?.Settings.SelectedPage);
        if (Main is { } main) _ = main.Settings.Card.RefreshAsync();
    }

    // A drive arriving or leaving rereads the SD card once Windows has mounted it.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _WM_DEVICECHANGE
            && (int)wParam is _DBT_DEVICEARRIVAL or _DBT_DEVICEREMOVECOMPLETE)
        {
            _cardRefresh.Stop();
            _cardRefresh.Start();
        }
        return IntPtr.Zero;
    }

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
    // failure. A DS ROM copy to the card is cancelled and the window closes once its temp file
    // is removed. Cancels every build, then stays open until running builds stop, so none is
    // cut off while it replaces a CIA.
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_waitingForBuilds || _waitingForCopy
            || Main?.Settings.ReceiversActionCommand.IsRunning == true)
        {
            e.Cancel = true;
            return;
        }
        if (Main is { IsCopying: true } copying)
        {
            e.Cancel = true;
            _waitingForCopy = true;
            IsEnabled = false;
            copying.Shutdown();
            copying.StatusMessage = "Closing when the SD card copy stops";
            copying.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(MainViewModel.IsCopying) || copying.IsCopying) return;
                _waitingForCopy = false;
                Dispatcher.BeginInvoke(Close);
            };
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
