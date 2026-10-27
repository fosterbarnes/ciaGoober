using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CiaGoober.Tabs;

// The base tab bar (base/src/base/windows/MainWindow.xaml.cs): sliding selected indicator,
// dividers, tab text color, and a fade between the page of each tab. TabItem.Tag names the tab;
// pages maps each name to its page.
public sealed class TabBar
{
    private static readonly TimeSpan _TRANSITION = TimeSpan.FromMilliseconds(180);

    private readonly TabControl _tabs;
    private readonly IReadOnlyDictionary<string, FrameworkElement> _pages;
    private FrameworkElement? _activePage;
    private int _pageTransition;
    private bool _ready;

    public TabBar(TabControl tabs, IReadOnlyDictionary<string, FrameworkElement> pages)
    {
        _tabs = tabs;
        _pages = pages;
        tabs.SelectionChanged += Tabs_SelectionChanged;
        tabs.SizeChanged += (_, _) => UpdateRowLayout(false);
    }

    public string? SelectedName => (_tabs.SelectedItem as TabItem) is { } tab ? Name(tab) : null;

    // Call once the window is loaded: selects the named tab (first tab when unknown) and shows
    // its page without animation.
    public void Start(string? name)
    {
        _tabs.SelectedItem = Find(name) ?? _tabs.Items[0];
        _ready = true;
        UpdateForegrounds(false);
        UpdateRowLayout(false);
        ShowPage(false);
    }

    public void Select(string name)
    {
        if (Find(name) is { } tab) _tabs.SelectedItem = tab;
    }

    private TabItem? Find(string? name) =>
        _tabs.Items.OfType<TabItem>().FirstOrDefault(tab => Name(tab) == name);

    private static string Name(TabItem tab) => tab.Tag.ToString()!;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // ComboBox and ListBox selection events bubble through the TabControl; only react to
        // tab changes.
        if (e.Source != _tabs || !_ready)
            return;

        UpdateForegrounds(true);
        UpdateRowLayout(true);
        ShowPage(true);
    }

    private void UpdateRowLayout(bool animate)
    {
        if (_tabs.SelectedIndex < 0 || _tabs.Items.Count == 0)
            return;

        var headerHost = _tabs.Template.FindName("HeaderHost", _tabs) as FrameworkElement;
        var dividerLayer = _tabs.Template.FindName("TabDividers", _tabs) as Panel;
        var indicator = _tabs.Template.FindName("SelectedTabIndicator", _tabs) as Border;
        var transform = _tabs.Template.FindName("SelectedTabIndicatorTransform", _tabs) as TranslateTransform;
        if (headerHost is null || dividerLayer is null || indicator is null || transform is null || headerHost.ActualWidth <= 0)
            return;

        var tabCount = _tabs.Items.Count;
        var tabWidth = headerHost.ActualWidth / tabCount;

        indicator.Width = tabWidth;
        UpdateDividers(dividerLayer, tabWidth, tabCount);
        if (animate)
        {
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(_tabs.SelectedIndex * tabWidth, _TRANSITION)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                },
                HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = _tabs.SelectedIndex * tabWidth;
        }
    }

    private static void UpdateDividers(Panel dividerLayer, double tabWidth, int tabCount)
    {
        dividerLayer.Children.Clear();
        var inset = (Thickness)dividerLayer.FindResource("TabDividerInset");
        var brush = (Brush)dividerLayer.FindResource("BorderSubtleBrush");
        for (var index = 1; index < tabCount; index++)
        {
            dividerLayer.Children.Add(new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(tabWidth * index, inset.Top, inset.Right, inset.Bottom),
                Background = brush,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            });
        }
    }

    private void ShowPage(bool animate)
    {
        if (SelectedName is not { } name || !_pages.TryGetValue(name, out var nextPage)
            || ReferenceEquals(nextPage, _activePage))
            return;

        var previousPage = _activePage;
        _activePage = nextPage;
        var transition = ++_pageTransition;

        nextPage.BeginAnimation(UIElement.OpacityProperty, null);
        nextPage.Visibility = Visibility.Visible;
        if (!animate || previousPage is null)
        {
            nextPage.Opacity = 1;
            foreach (var page in _pages.Values) HideInactivePage(page, nextPage);
            return;
        }

        previousPage.BeginAnimation(UIElement.OpacityProperty, null);
        previousPage.Visibility = Visibility.Visible;
        previousPage.Opacity = 1;
        nextPage.Opacity = 0;

        var fadeOut = new DoubleAnimation(1, 0, _TRANSITION);
        var fadeIn = new DoubleAnimation(0, 1, _TRANSITION);
        fadeIn.Completed += (_, _) =>
        {
            // A newer tab change owns the pages now.
            if (transition != _pageTransition)
                return;

            previousPage.Visibility = Visibility.Collapsed;
            previousPage.Opacity = 0;
        };
        previousPage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        nextPage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    private static void HideInactivePage(FrameworkElement page, FrameworkElement activePage)
    {
        if (ReferenceEquals(page, activePage))
            return;

        page.BeginAnimation(UIElement.OpacityProperty, null);
        page.Opacity = 0;
        page.Visibility = Visibility.Collapsed;
    }

    private void UpdateForegrounds(bool animate)
    {
        var selectedColor = ResourceColor("PrimaryTextBrush");
        var unselectedColor = ResourceColor("TertiaryTextBrush");
        foreach (var tab in _tabs.Items.OfType<TabItem>())
        {
            // Style brushes are frozen; give each tab its own animatable brush.
            if (tab.Foreground is not SolidColorBrush foreground)
                continue;

            var brush = foreground.IsFrozen ? foreground.Clone() : foreground;
            tab.Foreground = brush;
            AnimateColor(brush, tab.IsSelected ? selectedColor : unselectedColor, animate);
        }
    }

    private Color ResourceColor(string key) => ((SolidColorBrush)_tabs.FindResource(key)).Color;

    private static void AnimateColor(SolidColorBrush brush, Color color, bool animate)
    {
        brush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            animate
                ? new ColorAnimation(color, _TRANSITION)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                }
                : null);
        if (!animate)
            brush.Color = color;
    }
}
