using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CiaGoober.ViewModels;

namespace CiaGoober.Tabs;

// Settings tab. DataContext = SettingsViewModel.
public partial class SettingsTab : UserControl
{
    public SettingsTab()
    {
        InitializeComponent();
    }

    // Rechecks the SD card, GBA, GB/GBC, NES and SNES templates and LaunchBox data each time
    // the tab is shown.
    private async void SettingsTab_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && DataContext is SettingsViewModel settings)
            await settings.RefreshPageStateAsync();
    }

    // Text fields save on focus loss; Enter saves without leaving the field.
    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
