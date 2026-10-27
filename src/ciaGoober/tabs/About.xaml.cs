using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace CiaGoober.Tabs;

public partial class AboutTab : UserControl
{
    private readonly string _installPath = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar);

    public AboutTab()
    {
        InitializeComponent();
        AboutTitleText.Text = "ciaGoober (x64)";
        AboutVersionText.Text = $"v{ReadVersion()}";
        AboutInstallPathText.Text = _installPath;
        AboutSettingsPathText.Text = AppPaths.AppData;
    }

    private static string ReadVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Version");
        return File.ReadLines(path).First(line => !string.IsNullOrWhiteSpace(line)).Trim().TrimStart('v', 'V');
    }

    private void AboutOpenInstallLocation_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(_installPath);
    }

    private void AboutOpenSettingsLocation_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.AppData);
        OpenFolder(AppPaths.AppData);
    }

    private static void OpenFolder(string path)
    {
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void AboutHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }
}
