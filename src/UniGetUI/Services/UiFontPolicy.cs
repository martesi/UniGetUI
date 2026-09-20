extern alias DrawingCommon;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Services;

internal static class UiFontPolicy
{
    public static void Apply()
    {
        if (!Settings.Get(Settings.K.UseSystemUIFont)) return;
        try
        {
            string? family = DrawingCommon::System.Drawing.SystemFonts.MessageBoxFont?.FontFamily.Name;
            if (!string.IsNullOrWhiteSpace(family))
                Application.Current.Resources["ContentControlThemeFontFamily"] = new FontFamily(family);
        }
        catch (Exception ex)
        {
            Logger.Warn("The Windows message font could not be read; keeping the native WinUI font.");
            Logger.Warn(ex);
        }
    }
}
