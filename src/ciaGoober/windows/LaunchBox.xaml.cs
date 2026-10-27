using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CiaGoober.Helpers;
using CiaGoober.ViewModels;

namespace CiaGoober.Windows;

// LaunchBox image browser for one card's banner or icon (nds picker dialog).
public partial class LaunchBoxWindow : Window
{
    private readonly LaunchBoxViewModel _viewModel;

    private LaunchBoxWindow(GameViewModel game, CropTarget target)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTitleBarTheme.ApplyImmersiveDarkMode(this);
        Title = $"LaunchBox images: {game.DisplayName}";
        _viewModel = new LaunchBoxViewModel(game, target, AppPaths.LaunchBoxImages,
            AppPaths.LaunchBoxThumbs);
        _viewModel.Applied += (_, _) => DialogResult = true;
        DataContext = _viewModel;
    }

    // Modal over owner; true when an image was applied to the card.
    public static bool Open(Window owner, GameViewModel game, CropTarget target) =>
        new LaunchBoxWindow(game, target) { Owner = owner }.ShowDialog() == true;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync();
        if (!IsLoaded) return;
        if (_viewModel.CurrentGame is null) SearchBox.Focus();
        else FocusSelected(TabList);
    }

    private void Window_Closed(object? sender, EventArgs e) => _viewModel.Close();

    // KeyDown, not PreviewKeyDown, so an open ComboBox closes its list first.
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || !_viewModel.HasResults) return;
        e.Handled = true;
        ResultList.SelectedIndex = 0;
        FocusSelected(ResultList);
    }

    private void ResultList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is not ListBoxItem { DataContext: LaunchBoxGame game })
            return;
        e.Handled = true;
        PickGame(game);
    }

    private void Result_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (((ListBoxItem)sender).DataContext is LaunchBoxGame game) PickGame(game);
    }

    private void PickGame(LaunchBoxGame game)
    {
        _viewModel.ShowGame(game);
        FocusSelected(TabList);
    }

    private void TileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is not ListBoxItem { DataContext: LaunchBoxTile tile })
            return;
        e.Handled = true;
        _ = _viewModel.ApplyDefaultAsync(tile);
    }

    private void Tile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((ListBoxItem)sender).DataContext is LaunchBoxTile tile)
            _ = _viewModel.ApplyDefaultAsync(tile);
    }

    // Keyboard focus on the selected item, so arrow keys continue from it.
    private static void FocusSelected(ListBox list)
    {
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item)
            item.Focus();
        else
            list.Focus();
    }
}
