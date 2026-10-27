using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace EdifierManager;

public partial class MainWindow
{
    private string selectedTheme = "System";
    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        MenuButton.ContextMenu.PlacementTarget = MenuButton;
        MenuButton.ContextMenu.Placement = PlacementMode.Bottom;
        MenuButton.ContextMenu.IsOpen = true;
    }
    private void Help_Click(object sender, RoutedEventArgs e) => HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode }) return;
        ApplyTheme(mode);
        try { Directory.CreateDirectory(dataDirectory); File.WriteAllText(Path.Combine(dataDirectory, "theme.txt"), mode); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var item in MenuButton.ContextMenu.Items.OfType<MenuItem>())
            if (item.Tag is string tag) item.IsChecked = tag == mode;
    }
    private void ThemePreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!closing && selectedTheme == "System") Dispatcher.BeginInvoke(() => { if (!closing) ApplyTheme("System"); });
    }
    private void ApplyTheme(string mode)
    {
        selectedTheme = mode;
        // .NET 10 WPF still marks the runtime theme switch as experimental.
#pragma warning disable WPF0001
        Application.Current.ThemeMode = mode switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
#pragma warning restore WPF0001
        bool dark = mode == "Dark" || (mode == "System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0);
        string[] names = ["CanvasBrush", "SurfaceBrush", "InsetBrush", "StrokeBrush", "PrimaryBrush", "SecondaryBrush", "AccentBrush", "AccentSoftBrush", "AccentTextBrush"];
        string[] colors = dark ? ["#181C23", "#222832", "#29313D", "#3C4655", "#F0F3F8", "#ADB9CD", "#67D9CB", "#193D3D", "#103330"] : ["#F3F5F8", "#FFFFFF", "#F6F8FB", "#E0E5ED", "#202936", "#66758A", "#117D76", "#E6F5F2", "#FFFFFF"];
        for (int i = 0; i < names.Length; i++) Application.Current.Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        if (SystemParameters.HighContrast)
        {
            foreach (var name in names.Take(3)) Application.Current.Resources[name] = SystemColors.WindowBrush;
            foreach (var name in new[] { "PrimaryBrush", "SecondaryBrush", "StrokeBrush" }) Application.Current.Resources[name] = SystemColors.WindowTextBrush;
            Application.Current.Resources["AccentBrush"] = SystemColors.HighlightBrush;
            Application.Current.Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
        }
        LeftGauge?.InvalidateVisual(); RightGauge?.InvalidateVisual(); ProductArt?.InvalidateVisual();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            int darkValue = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref darkValue, sizeof(int));
        }
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
