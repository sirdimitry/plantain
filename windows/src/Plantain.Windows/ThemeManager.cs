using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Plantain.Windows;

internal static class ThemeManager
{
    internal static void Apply(ResourceDictionary resources, bool? dark = null)
    {
        if (SystemParameters.HighContrast)
        {
            resources["PageBrush"] = SystemColors.WindowBrush; resources["CardBrush"] = SystemColors.WindowBrush;
            resources["TextBrush"] = SystemColors.WindowTextBrush; resources["MutedBrush"] = SystemColors.WindowTextBrush;
            resources["BorderBrush"] = SystemColors.WindowTextBrush; resources["AccentBrush"] = SystemColors.HighlightBrush;
            resources["AccentTextBrush"] = SystemColors.HighlightTextBrush; resources["ErrorBrush"] = SystemColors.HighlightBrush;
            resources["TrackBrush"] = SystemColors.ControlBrush; resources["HoverBrush"] = SystemColors.ControlBrush;
            resources["ErrorBadgeBrush"] = SystemColors.HighlightBrush; resources["ErrorBadgeTextBrush"] = SystemColors.HighlightTextBrush;
            return;
        }
        var isDark = dark ?? IsDark();
        resources["ErrorBadgeBrush"] = new SolidColorBrush(Color.FromRgb(189, 32, 43));
        resources["ErrorBadgeTextBrush"] = Brushes.White;
        var values = isDark
            ? new[] { "#171C24", "#222A36", "#F2F5FA", "#B8C4D5", "#58677B", "#85B7FF", "#111B2B", "#FF8991", "#3E4B60", "#34465C" }
            : new[] { "#F4F6F9", "#FFFFFF", "#17202E", "#536176", "#BCC6D3", "#075CCC", "#FFFFFF", "#BD202B", "#E1E6EF", "#E5ECF6" };
        var names = new[] { "PageBrush", "CardBrush", "TextBrush", "MutedBrush", "BorderBrush", "AccentBrush", "AccentTextBrush", "ErrorBrush", "TrackBrush", "HoverBrush" };
        for (var i = 0; i < names.Length; i++) resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
    }

    private static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }
}
