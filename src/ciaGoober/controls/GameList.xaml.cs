using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using CiaGoober.ViewModels;

namespace CiaGoober.Controls;

// DataContext = GamesTabViewModel. The filter view lives here so the view models keep one list.
public partial class GameList : UserControl
{
    private readonly CollectionViewSource _games = new();
    private GamesTabViewModel? _tab;

    public GameList()
    {
        InitializeComponent();
        _games.Filter += Games_Filter;
        DataContextChanged += (_, _) => Attach(DataContext as GamesTabViewModel);
    }

    private void Attach(GamesTabViewModel? tab)
    {
        if (_tab is not null) _tab.PropertyChanged -= Tab_PropertyChanged;
        _tab = tab;
        _games.Source = tab?.Main.Games;
        GameRows.ItemsSource = _games.View;
        if (tab is not null) tab.PropertyChanged += Tab_PropertyChanged;
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GamesTabViewModel.Filter)) _games.View?.Refresh();
    }

    private void Games_Filter(object sender, FilterEventArgs e)
    {
        var game = (GameViewModel)e.Item;
        var text = _tab?.Filter.Trim() ?? "";
        e.Accepted = game.System == _tab?.System
            && (text == "" || game.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase));
    }
}
